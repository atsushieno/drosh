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
		static NDKType [] target_ndks = new NDKType [] { NDKType.R5, NDKType.CrystaxR4b, NDKType.R4b };
		static ArchType [] target_archs = new ArchType [] { ArchType.Arm, ArchType.ArmV7a, ArchType.X86 };

		public static void QueueBuild (ProjectRevision rev, UserReference user)
		{
			if (rev == null)
				throw new ArgumentNullException ("rev");

			var project = DataStore.GetProjectWithRevision (rev.ProjectOwner, rev.ProjectName, rev.RevisionId);
			if (project == null)
				throw new Exception (String.Format ("Project {0}/{1} for ProjectRevision {2} was not found", rev.ProjectOwner, rev.ProjectName, rev.RevisionId));
			if (project.Owner != user && !project.Builders.Contains (user))
				throw new Exception (String.Format ("User {0} is not granted to build this project", user));

			foreach (var ndkType in target_ndks) {
				if ((project.TargetNDKs & ndkType) == 0)
					continue;
				foreach (var archType in target_archs) {
					if ((project.TargetArchs & archType) == 0)
						continue;

					var build = new BuildRecord () {
						BuildId = Guid.NewGuid ().ToString (),
						ProjectOwner = rev.ProjectOwner,
						ProjectName = rev.ProjectName,
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

		public static void ProcessBuilds ()
		{
			DateTime lastBuild = DateTime.MinValue;
			foreach (var build in DataStore.Builds.Where (b => b.Status == BuildStatus.Queued)) {
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

		public static void ProcessBuild (BuildRecord build)
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

			var project = DataStore.GetProjectWithRevision (build.ProjectOwner, build.ProjectName, build.ProjectRevision);
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
			if (Directory.Exists (buildDir))
				Directory.Delete (buildDir, true);
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
					var deppath = Path.Combine (Drosh.DownloadTopdir, "user", dp.Owner, b.LocalResultArchive);
					Unpack (deppath, depsDir);
				}
			}

			string path = Path.Combine (Drosh.DownloadTopdir, "user", build.ProjectOwner, project.LocalArchiveName);
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
				string script = scriptObj != null ? scriptObj.Text : GetDefaultScript (project.BuildType, buildStep, build.TargetNDK);

				string scriptFile = Path.Combine (actualSrcDir, String.Format ("__build_command_{0}.sh", buildStep));
				using (var fs = File.CreateText (scriptFile))
					fs.WriteLine (script);
				var psi = new ProcessStartInfo () { FileName = "bash", Arguments = scriptFile, WorkingDirectory = actualSrcDir, UseShellExecute = false };
				psi.EnvironmentVariables.Add ("ANDROID_NDK_ROOT", Drosh.GetAndroidRoot (build.TargetNDK));
				psi.EnvironmentVariables.Add ("DEPS_TOPDIR", depsDir);
				psi.EnvironmentVariables.Add ("RESULT_TOPDIR", resultDir);
				psi.EnvironmentVariables.Add ("RUNNER_DIR", Drosh.ToolDir);
				var proc = Process.Start (psi);
				if (!proc.WaitForExit (1000 * 60 * 10)) {
					proc.Kill ();
					throw new Exception (String.Format ("Forcibly terminated build step: {0}", buildStep));
				}
				if (proc.ExitCode != 0)
					throw new Exception (String.Format ("Process error at build step: {0} with exit code {1}", buildStep, proc.ExitCode));
			}

			// Now that build and install is done successfully, pack the results into an archive.

			// FIXME: handle filesbypkg

			string destArc = Path.Combine (buildDir, build.ProjectName + "-bin.tar.bz2");
			var pkpsi = new ProcessStartInfo () { FileName = "tar", Arguments = String.Format ("jcf {0} build", destArc), WorkingDirectory = buildDir, UseShellExecute = false };
			var pkproc = Process.Start (pkpsi);
			if (!pkproc.WaitForExit (10000)) {
				pkproc.Kill ();
				throw new Exception (String.Format ("Forcibly terminated packing step."));
			}
			string dlname = Path.Combine ("user", project.Owner, Guid.NewGuid () + "_" + Path.GetFileName (destArc));
			File.Move (destArc, Path.Combine (Drosh.DownloadTopdir, dlname));
			build.PublicResultArchive = Path.GetFileName (destArc);
			build.LocalResultArchive = dlname;
			build.Status = BuildStatus.Success;
			build.BuildFinishedTimestamp = DateTime.Now;
			DataStore.UpdateBuildRecord (build);
		}

		static readonly ScriptStep [] build_steps = new ScriptStep [] { ScriptStep.Build, ScriptStep.PreInstall, ScriptStep.Install, ScriptStep.PostInstall };

		static string GetDefaultScript (BuildType type, ScriptStep step, NDKType ndk)
		{
			return File.ReadAllText (Path.Combine (Drosh.BuildTopdir, String.Format ("__default_script_{0}_{1}_{2}.txt", type, step, ndk)));
		}

		static void Unpack (string archive, string destDir)
		{
			Process proc;
			var psi = new ProcessStartInfo () { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
			psi.WorkingDirectory = destDir;
			switch (Path.GetExtension (archive).ToLower ()) {
			case ".zip":
				psi.FileName = "unzip";
				psi.Arguments = '"' + archive + '"';
				proc = Process.Start (psi);
				break;
			case ".gz":
				psi.FileName = "tar";
				psi.Arguments = "zxvf \"" + archive + '"';
				proc = Process.Start (psi);
				break;
			case ".bz2":
				psi.FileName = "tar";
				psi.Arguments = "jxvf \"" + archive + '"';
				proc = Process.Start (psi);
				break;
			default:
				throw new NotSupportedException (String.Format ("Not supported extension: {0}", Path.GetExtension (archive).ToLower ()));
			}
			if (!proc.WaitForExit (10000)) {
				proc.Kill ();
				throw new Exception (String.Format ("Forcibly terminated {0} for timeout.", psi.FileName));
			}
		}
	}
}
