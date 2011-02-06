using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;

using ProjectReference = System.String; // Project.Id
using UserReference = System.String; // user.Name

namespace drosh
{
	public class Builder
	{
		NDKType [] target_ndks = new NDKType [] { NDKType.R5, NDKType.CrystaxR4b, NDKType.R4b };
		ArchType [] target_archs = new ArchType [] { ArchType.Arm, ArchType.ArmV7a, ArchType.X86 };

		public void QueueBuild (ProjectRevision rev, UserReference user)
		{
			if (rev == null)
				throw new ArgumentNullException ("rev");

			var project = DataStore.GetProjectWithRevision (rev.Project, rev.RevisionId);
			if (project == null)
				throw new Exception (String.Format ("Project {0} for ProjectRevision {1} was not found", rev.Project, rev.RevisionId));
			
			foreach (var ndkType in target_ndks) {
				if ((project.TargetNDKs & ndkType) == 0)
					continue;
				foreach (var archType in target_archs) {
					if ((project.TargetArchs & archType) == 0)
						continue;

					var build = new BuildRecord () {
						BuildId = Guid.NewGuid ().ToString (),
						Project = rev.Project,
						ProjectRevision = rev.RevisionId,
						TargetNDK = ndkType,
						TargetArch = archType,
						Builder = user,
						Status = BuildStatus.Queued,
						};

					build.BuildRecordedTimestamp = DateTime.Now;
					DataStore.RegisterBuildRecord (build);
				}
			}
		}

		public void ProcessBuilds ()
		{
			BuildRecord build;
			DateTime lastBuild = DateTime.MinValue;
			while ((build = DataStore.Builds.FirstOrDefault (b => b.Status == BuildStatus.Queued)) != null) {
				if (DateTime.Now - lastBuild < TimeSpan.FromMilliseconds (100))
					break; // too short. Very likely something bad happens.
				if (build == null)
					break; // no more pending build.
				if (build.Status != BuildStatus.Queued) // likely synchonization issue
					continue;

				lastBuild = DateTime.Now;
				ProcessBuild (build);
			}
		}

		public void ProcessBuild (BuildRecord build)
		{
/*
    * It creates a dist directory for each build(-id). Name it as [topdir]. The directory layout looks like:

    * build
    * deps
    * src
          o target-src.[tar.gz|tar.bz2|tar.xz|zip]
          o downloaded-deps-outputs.tar.bz2 …

    * Then it pulls target project source and deps. Only a limited kinds of packages are supported. target source is expanded under src. Depds are immediately under [topdir].
    * Then it adds deps/bin to $PATH, deps/lib to $LD_LIBRARY_PATH, deps/lib/pkgconfig to $PKG_CONFIG_PATH.
    * Then it goes to src/{subdir} (in case there is only one subdir) or src (in case not found). Then it runs build script (typically configure and make), preinstall script, install script (typically make install) and then postinstall script, in order, where the runner gives these environment variables:
          o ANDROID_NDK_ROOT: android NDK topdir
          o DEPS_TOPDIR: the topdir for all extracted deps. PATH, LD_LIBRARY_PATH and PKG_CONFIG_PATH are added for this dir.
          o RESULT_TOPDIR: the installation directory. Only files in this dir will be included in the results archive.
    * Once all of them are successfully done, then the server packages everything in the topdir/build into the output archive, and then push to file server (so far it is just a copy to www dir, could be replaced with push to amazon s3 etc.).
          o FIXME: should deps included in the resulting archive too?
    * Then the server pushes the result to the registered sharehouses.
*/

			var project = DataStore.GetProjectWithRevision (build.Project, build.ProjectRevision);
			if (project == null)
				throw new Exception ("Project was not found");
			build.Status = BuildStatus.Ongoing;
			build.BuildStartedTimestamp = DateTime.Now;
			DataStore.UpdateBuildRecord (build);


			// Create a build directory
			string buildDir = Path.Combine (Drosh.BuildTopdir, build.BuildId);
			string resultDir = Path.Combine (buildDir, "build");
			string depsDir = Path.Combine (buildDir, "deps");
			string buildSrcDir = Path.Combine (buildDir, "src");
			Directory.CreateDirectory (buildDir);
			Directory.CreateDirectory (resultDir);
			Directory.CreateDirectory (depsDir);
			Directory.CreateDirectory (buildSrcDir);
			
			// pull source and deps

			if (project.Dependencies != null) {
				foreach (var dep in project.Dependencies) {
					var dp = DataStore.GetProject (dep);
					var b = DataStore.GetLatestBuild (dp, build.TargetArch);
					if (b == null)
						throw new Exception (String.Format ("Dependency project {0}/{1} has no successful result for {2} yet", dp.Owner, dp.Name, build.TargetArch));
					var deppath = Path.Combine (Drosh.DownloadTopdir, dp.Owner, b.LocalResultArchive);
					Unpack (deppath, depsDir);
				}
			}

			string path = Path.Combine (Drosh.DownloadTopdir, project.LocalArchiveName);
			string srcCopied = Path.Combine (buildSrcDir, Path.GetFileName (path));
			File.Copy (path, srcCopied);
			Unpack (srcCopied, buildSrcDir);
			var dirs = Directory.GetDirectories (buildSrcDir);
			string actualSrcDir = dirs.Length == 1 ? dirs [0] : buildSrcDir;

			foreach (var patch in from p in project.Patches where (p.TargetNDKs & build.TargetNDK) != 0 && (p.TargetArchs & build.TargetArch) != 0 select p) {
				var patchFile = Path.Combine (actualSrcDir, String.Format ("__drosh_patch_{0}_{1}.patch", build.TargetNDK, build.TargetArch));
				using (var fs = File.CreateText (patchFile))
					fs.Write (patch.Text);
				var psi = new ProcessStartInfo () { FileName = "patch", Arguments = "-i -p0 \"" + patchFile + "\"", WorkingDirectory = actualSrcDir };
				var proc = Process.Start (psi);
				if (!proc.WaitForExit (10000)) {
					proc.Kill ();
					throw new Exception ("Forcibly terminated patch.");
				}
			}

			// Go to srcdir and start build

			foreach (var buildStep in build_steps) {
				var scriptObj = project.Scripts.FirstOrDefault (s => s.Step == buildStep && (s.TargetNDKs & build.TargetNDK) != 0 && (s.TargetArchs & build.TargetArch) != 0);
				string script = scriptObj != null ? scriptObj.Text : GetDefaultScript (buildStep, build.TargetNDK);

				var psi = new ProcessStartInfo () { WorkingDirectory = actualSrcDir };
				psi.EnvironmentVariables.Add ("ANDROID_NDK_ROOT", Drosh.GetAndroidRoot (build.TargetNDK));
				psi.EnvironmentVariables.Add ("DEPS_TOPDIR", depsDir);
				psi.EnvironmentVariables.Add ("RESULT_TOPDIR", resultDir);
				var proc = Process.Start (script);
				if (!proc.WaitForExit (1000 * 60 * 10)) {
					proc.Kill ();
					throw new Exception (String.Format ("Forcibly terminated build step: {0}", buildStep));
				}
			}

			// Now that build and install is done successfully, pack the results into an archive.

			string destArc = Path.Combine (buildDir, build.Project + "-bin.tar.bz2");
			var pkpsi = new ProcessStartInfo () { FileName = "tar", Arguments = String.Format ("jcf {0} {1}/*", destArc, resultDir) };
			var pkproc = Process.Start (pkpsi);
			if (!pkproc.WaitForExit (10000)) {
				pkproc.Kill ();
				throw new Exception (String.Format ("Forcibly terminated packing step."));
			}
			string dlname = Path.Combine (project.Owner, Guid.NewGuid () + "_" + Path.GetFileName (destArc));
			File.Move (destArc, Path.Combine (Drosh.DownloadTopdir, dlname));
			build.PublicResultArchive = Path.GetFileName (destArc);
			build.LocalResultArchive = dlname;
			build.Status = BuildStatus.Success;
			build.BuildFinishedTimestamp = DateTime.Now;
			DataStore.UpdateBuildRecord (build);
		}

		static readonly ScriptStep [] build_steps = new ScriptStep [] { ScriptStep.Build, ScriptStep.PreInstall, ScriptStep.Install, ScriptStep.PostInstall };

		string GetDefaultScript (ScriptStep step, NDKType ndk)
		{
			return File.ReadAllText (Path.Combine (Drosh.BuildTopdir, String.Format ("__default_script_{0}_{1}.txt", step, ndk)));
		}

		void Unpack (string archive, string destDir)
		{
			Process proc;
			var psi = new ProcessStartInfo () { UseShellExecute = true, RedirectStandardOutput = true, RedirectStandardError = true };
			psi.WorkingDirectory = destDir;
			switch (Path.GetExtension (archive).ToLower ()) {
			case "zip":
				psi.FileName = "unzip";
				psi.Arguments = '"' + archive + '"';
				proc = Process.Start (psi);
				break;
			case "bz2":
				psi.FileName = "tar";
				psi.Arguments = "jxvf \"" + archive + '"';
				Process.Start (psi);
				break;
			default:
				throw new NotSupportedException ();
			}
			if (!proc.WaitForExit (10000)) {
				proc.Kill ();
				throw new Exception (String.Format ("Forcibly terminated {0} for timeout.", psi.FileName));
			}
		}
	}
}
