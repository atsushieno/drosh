using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using Manos;
using Manos.Http;
using Manos.Spark;

namespace drosh
{
	static class Extensions
	{
		public static V Get<K,V> (this Dictionary<K,V> dic, K key)
		{
			return dic.ContainsKey (key) ? dic [key] : default (V);
		}
	}

	public enum UserManagementMode
	{
		New,
		Confirm,
		Update
	}

	public enum ProjectManagementMode
	{
		New,
		Confirm,
		Update
	}

	public class DroshSession
	{
		public DroshSession (string id, User user)
		{
			Id = id;
			User = user;
		}
		
		public string Id { get; set; }
		public User User { get; set; }
		public string Notification { get; set; }

		public string PullNotification ()
		{
			var ret = Notification;
			Notification = null;
			return ret;
		}
	}

	public class DroshWeb : ManosApp
	{
		static ArchType [] target_archs = new ArchType [] { ArchType.Arm, ArchType.ArmV7a, ArchType.X86 };
		static readonly int id_prefix_length = Guid.NewGuid ().ToString ().Length;
		public static string DownloadTopdir {
			get { return Drosh.DownloadTopdir; }
		}

		public DroshWeb ()
		{
			Route ("/default.css", new StaticContentModule ());
			Route ("/images/", new StaticContentModule ());
		}

		DroshSession GetSession (IManosContext ctx)
		{
			var sessionId = ctx.Request.Cookies.Get ("drosh-session");
			var ret = sessionId != null ? GetSessionCache (sessionId) : null;
			return ret;
		}

		DroshSession GetSessionCache (string key)
		{
			if (key == null)
				return null;
			object ret = null;
			Cache.Get (key, delegate (string k, object value) { ret = value; });
			return (DroshSession) ret;
		}

		void AssertLoggedIn (IManosContext ctx, Action<IManosContext,DroshSession> action)
		{
			var session = GetSession (ctx) ?? new DroshSession (Guid.NewGuid ().ToString (), null);
			if (session.User == null) {
				session.Notification = "Login status expired or not logged in";
				SetSession (ctx, session);
				ctx.Response.Redirect ("/");
			}
			else
				action (ctx, session);
		}
		
		void SetSession (IManosContext ctx, DroshSession session)
		{
			ctx.Response.SetCookie ("drosh-session", new HttpCookie ("drosh-session", session.Id) {Path = "/"});
			Cache.Set (session.Id, session, TimeSpan.FromMinutes (60));
		}
		
		[Route ("/", "/index", "/home")]
		public void Index (IManosContext ctx)
		{
			var session = GetSession (ctx);
			if (session == null || session.User == null)
				NotLogged (ctx);
			else
				LoggedHome (ctx, session);
		}

		void NotLogged (IManosContext ctx)
		{
			var session = GetSession (ctx);
			string notification = session != null ? session.PullNotification () : null;
			this.RenderSparkView (ctx, "Home.spark", new { Notification = notification});
			ctx.Response.End ();
		}

		[Route ("/login")]
		public void Login (IManosContext ctx, string link)
		{
			var session = GetSession (ctx) ?? new DroshSession (Guid.NewGuid ().ToString (), null);
			if (session.User == null) {
				var userid = ctx.Request.Data ["login_user"];
				var passraw = ctx.Request.Data ["login_password"];
				var user = DataStore.GetUser (userid);
				bool error = user == null || passraw == null || user.PasswordHash != DataStore.HashPassword (passraw);
				session.Notification = error ? String.Format ("User name '{0}' does not exist or password is wrong", userid) : String.Format ("Welcome, {0}!", user.Name);
				session.User = error ? null : user;
				SetSession (ctx, session);
				if (error) {
					ctx.Response.Redirect ("/");
					return;
				}
			}

			ctx.Response.Redirect (link ?? "/");
		}

		[Route ("/logout")]
		public void Logout (IManosContext ctx)
		{
			var session = GetSession (ctx) ?? new DroshSession (Guid.NewGuid ().ToString (), null);
			session.Notification = (session.User != null) ? "loggeed out" : "not logged in";
			session.User = null;
			SetSession (ctx, session);
			ctx.Response.Redirect ("/");
		}

		// User registration

		[Route ("/register/user/new")]
		public void StartUserRegistration (IManosContext ctx)
		{
			var session = GetSession (ctx);
			string notification = session != null ? session.PullNotification () : null;
			this.RenderSparkView (ctx, "ManageUser.spark", new {ManagementMode = UserManagementMode.New, Editable = true, Notification = notification, User = CreateUserFromForm (ctx)});
			ctx.Response.End ();
		}

		[Route ("/register/user/confirm")]
		public void ConfirmUserRegistration (IManosContext ctx)
		{
			// FIXME: validate inputs more.
			string error = null;
			if (ctx.Request.Data ["password"] != ctx.Request.Data ["password-verified"])
				error = "Password inputs don't match.";
			else if (DataStore.GetUser (ctx.Request.Data ["username"]) != null)
				error = "The same user name is already registered. Please pick another name.";
			var user = CreateUserFromForm (ctx);
			var mode = error == null ? UserManagementMode.Confirm : UserManagementMode.New;
			user.PasswordHash = DataStore.HashPassword (ctx.Request.Data ["password"]);
			this.RenderSparkView (ctx, "ManageUser.spark", new {ManagementMode = mode, User = user, Editable = (error != null), Notification = error});
			ctx.Response.End ();
		}

		[Route ("/register/user/register")]
		public void ExecuteUserRegistration (IManosContext ctx)
		{
			var existingSession = GetSession (ctx) ?? new DroshSession (Guid.NewGuid ().ToString (), null);
			if (existingSession.User != null) {
				existingSession.Notification = "already logged in";
				ctx.Response.Redirect ("/");
				return;
			}

			// FIXME: validate inputs.

			var user = CreateUserFromForm (ctx);
			user.Status = UserStatus.Pending;
			user.Verification = Guid.NewGuid ().ToString ();
			DataStore.RegisterUser (user);
#if MAIL
			MailClient.SendRegistration (user);
			session.Notification = "Confirmation email will be sent to your inbox";
			ctx.Response.Redirect ("/");
#else
			VerifyUserRegistration (ctx, user);
#endif
		}

		[Route ("/register/user/verify/{userid}/{verification}")]
		public void VerifyUserRegistration (IManosContext ctx, string userid, string verification)
		{
			var session = GetSession (ctx) ?? new DroshSession (Guid.NewGuid ().ToString (), null);
			var user = DataStore.GetUserByVerificationCode (userid, verification);
			session.User = user;
			session.Notification = (user == null) ? "Invalid verification code." : (user.Status != UserStatus.Pending) ? "The user is not either registered or in pending state." : null;
			if (session.Notification != null)
				ctx.Response.Redirect ("/");
			else
				VerifyUserRegistration (ctx, user);
		}

		void VerifyUserRegistration (IManosContext ctx, User user)
		{
			user.Status = UserStatus.Active;
			DataStore.UpdateUser (user);
			string path = Path.Combine (DownloadTopdir, "user", user.Name);
			if (!Directory.Exists (path))
				Directory.CreateDirectory (path);

			string sessionId = Guid.NewGuid ().ToString ();
			var session = new DroshSession (sessionId, user);
			session.Notification = "You are now registered";
			SetSession (ctx, session);
			ctx.Response.Redirect ("/");
		}

		[Route ("/register/user/edit")]
		public void StartUserUpdate (IManosContext ctx)
		{
			AssertLoggedIn (ctx, (c, session) => StartUserUpdate (c, session));
		}

		void StartUserUpdate (IManosContext ctx, DroshSession session)
		{
			this.RenderSparkView (ctx, "ManageUser.spark", new {Session = session, ManagementMode = UserManagementMode.Update, User = session.User, LoggedUser = session.User, Editable = true, Notification = session.PullNotification ()});
			ctx.Response.End ();
		}

		[Route ("/register/user/update")]
		public void ExecuteUserUpdate (IManosContext ctx, DroshSession session)
		{
			var user = CreateUserFromForm (ctx);
			// process password change request with care: check existing password
			var rawpwd = ctx.Request.Data ["old-password"];
			if (ctx.Request.Data ["password"] != null && ctx.Request.Data ["password"] != ctx.Request.Data ["password-verified"] || rawpwd != null && DataStore.HashPassword (rawpwd) != user.PasswordHash) {
				this.RenderSparkView (ctx, "ManageUser.spark", new {Session = session, ManagementMode = UserManagementMode.Update, User = user, LoggedUser = user, Editable = true, Notification = "Password didn't match"});
			} else {
				user.PasswordHash = DataStore.HashPassword (ctx.Request.Data ["password"]) ?? user.PasswordHash;
				DataStore.UpdateUser (user);
				this.RenderSparkView (ctx, "ManageUser.spark", new {ManagementMode = UserManagementMode.Update, User = user, LoggedUser = user, Editable = true, Notification = "updated!"});
			}
			ctx.Response.End ();
		}

		[Route ("/register/user/delete")]
		public void DeleteUser (IManosContext ctx)
		{
			AssertLoggedIn (ctx, (c, session) => DeleteUser (c, session));
		}

		void DeleteUser (IManosContext ctx, DroshSession session)
		{ 
			if (ctx.Request.Data ["delete"] == null) {
				session.Notification = "Make sure to check required item.";
				StartUserUpdate (ctx, session);
			} else {
				DataStore.DeleteUser (session.User.Name);
				session.User = null;
				session.Notification = "Your account is now removed";
				ctx.Response.Redirect ("/");
			}
		}

		[Route ("/register/user/recovery")]
		public void StartPasswordRecovery (IManosContext ctx)
		{
			var session = GetSession (ctx);
			var notification = session != null ? session.PullNotification () : null;
			this.RenderSparkView (ctx, "PasswordRecovery.spark", new {Notification = notification});
			ctx.Response.End ();
		}

		[Route ("/register/user/recovery/execute")]
		public void ExecutePasswordRecovery (IManosContext ctx)
		{
			var name = ctx.Request.Data ["userid"];
			var user = DataStore.GetUser (name);
			var session = new DroshSession (Guid.NewGuid ().ToString (), null);
			if (user == null) {
				session.Notification = String.Format ("User '{0}' was not found", name);
				SetSession (ctx, session);
				ctx.Response.Redirect ("/register/user/recovery");
			} else {
				//user.Status = UserStatus.Pending;
				//user.Verification = Guid.NewGuid ().ToString ();
				//DataStore.UpdateUser (user);
				//MailManager.SendActivation (user);
				user.PasswordHash = DataStore.HashPassword (String.Empty);
				DataStore.UpdateUser (user);

				session.Notification = "<del>Your account is temporarily suspended and verification email is sent to you</del> Your password is reset to be empty. Change it immediately.";
				SetSession (ctx, session);
				ctx.Response.Redirect ("/");
			}
		}

		User CreateUserFromForm (IManosContext ctx)
		{
			var name = ctx.Request.Data ["username"];
			var u = new User ();
			var existing = name != null ? DataStore.GetUser (name) : null;
			if (existing != null) {
				u.PasswordHash = existing.PasswordHash;
				u.RegisteredTimestamp = existing.RegisteredTimestamp;
				u.Status = existing.Status;
			}
			else 
				u.PasswordHash = ctx.Request.Data ["password-hash"];
			u.Name = name;
			u.OpenID = ctx.Request.Data ["openid"];
			u.Profile = ctx.Request.Data ["profile"];

			return u;
		}

		void LoggedHome (IManosContext ctx, DroshSession session)
		{
			this.RenderSparkView (ctx, "Home.spark", new { Session = session, LoggedUser = session.User, Notification = session.PullNotification (), Builds = DataStore.GetLatestBuildsByUser (session.User.Name, 0, 10), Projects = DataStore.GetProjectsByUser (session.User.Name) });
			ctx.Response.End ();
		}
		
		// anonymous accesses
		
		[Route ("/download/{filename}")]
		public void DownloadFile (IManosContext ctx, string filename)
		{
			var path = Path.Combine (DownloadTopdir, filename);

			if (File.Exists (path)) {
				ctx.Response.Headers.SetNormalizedHeader ("Content-Type", ManosMimeTypes.GetMimeType (path));
				ctx.Response.Headers.SetNormalizedHeader ("Content-Disposition", "attachment;filename=" + filename.Substring (filename.LastIndexOf ('/') + 1).Substring (id_prefix_length + 1));
				ctx.Response.SendFile (path);
			} else
				ctx.Response.StatusCode = 404;
			ctx.Response.End ();
		}
		
		[Route ("/user/{userid}")]
		public void ShowUserDetails (IManosContext ctx, string userid)
		{
			var session = GetSession (ctx) ?? new DroshSession (Guid.NewGuid ().ToString (), null);
			var targetUser = DataStore.GetUser (userid);
			if (targetUser == null) {
				session.Notification = String.Format ("User {0} does not exist", userid);
				ctx.Response.Redirect ("/");
				return;
			}

			this.RenderSparkView (ctx, "UserDetails.spark", new { User = targetUser, LoggedUser = session.User, Notification = session.PullNotification (), InvolvedProjects = DataStore.GetProjectsByUser (userid) });
			ctx.Response.End ();
		}

		[Route ("/project/{userid}/{projectname}/{revision}",
			"/project/{userid}/{projectname}",
			"/project-by-id/{projectId}/{revision}",
			"/project-by-id/{projectId}")]
		public void ShowProjectDetails (IManosContext ctx, string userid, string projectname, string projectId, string revision, string notification)
		{
			var session = GetSession (ctx) ?? new DroshSession (Guid.NewGuid ().ToString (), null);
			// FIXME: pull specific revision
			var project = projectname != null ? DataStore.GetProject (userid, projectname) : DataStore.GetProject (projectId);
			if (project == null) {
				session.Notification = String.Format ("Project '{0}' was not found. Make sure that the link is correct.", projectId ?? userid + "/" + projectname);
				ctx.Response.Redirect ("/");
			} else {
				var builds = DataStore.GetLatestBuildsByProject (project.Owner, project.Name, 0, 10);
				var revs = DataStore.GetRevisions (project.Owner, project.Name, 0, 10);
				var dlbuilds = new List<BuildRecord> ();
				BuildRecord dlbuild = null;
				foreach (var arch in target_archs)
					if ((dlbuild = DataStore.GetLatestBuild (project, arch)) != null)
						dlbuilds.Add (dlbuild);

				// FIXME: better represented as ProjectRevision
				var forkOrigin = project.ForkOrigin != null ? DataStore.GetProject (project.ForkOrigin) : null;

				this.RenderSparkView (ctx, "Project.spark", new { Session = session, LoggedUser = session == null ? null : session.User, Notification = notification, Project = project, ForkOrigin = forkOrigin, Builds = builds, DownloadableBuilds = dlbuilds, Revisions = revs});

				ctx.Response.End ();
			}
		}
		
		[Route ("/projects")]
		public void SearchProjects (IManosContext ctx, string keyword)
		{
			var session = GetSession (ctx);
			var projects = DataStore.GetProjectsByKeyword (keyword, 0, 10);
			this.RenderSparkView (ctx, "Projects.spark", new { LoggedUser = session != null ? session.User : null, Notification = session != null ? session.Notification : null, SearchKeyword = keyword, Projects = projects });
			ctx.Response.End ();
		}

		// Project Management
		
		[Route ("/register/project/new")]
		public void StartProjectRegistration (IManosContext ctx, string notification)
		{
			AssertLoggedIn (ctx, (c, session) => StartProjectRegistration (c, session, notification));
		}
		
		void StartProjectRegistration (IManosContext ctx, DroshSession session, string notification)
		{
			this.RenderSparkView (ctx, "ManageProject.spark", new {Session = session, ManagementMode = ProjectManagementMode.New, Editable = true, Notification = notification, LoggedUser = session.User});
			ctx.Response.End ();
		}

		[Route ("/register/project/confirm")]
		public void ConfirmProjectRegistration (IManosContext ctx)
		{
			AssertLoggedIn (ctx, (c, session) => ConfirmProjectRegistration (c, session));
		}
		
		void ConfirmProjectRegistration (IManosContext ctx, DroshSession session)
		{
			// FIXME: validate inputs more.
			var project = CreateProjectFromForm (session, ctx);
			this.RenderSparkView (ctx, "ManageProject.spark", new {Session = session, ManagementMode = ProjectManagementMode.Confirm, LoggedUser = session.User, Editable = false, Project = project});
			ctx.Response.End ();
		}

		[Route ("/register/project/register")]
		public void ExecuteProjectRegistration (IManosContext ctx)
		{
			AssertLoggedIn (ctx, (c, session) => ExecuteProjectRegistration (c, session));
		}
		
		void ExecuteProjectRegistration (IManosContext ctx, DroshSession session)
		{
			// FIXME: validate inputs.

			var user = session.User;
			var project = CreateProjectFromForm (session, ctx);
			project.Owner = user.Name;
			project.Id = Guid.NewGuid ().ToString ();
			if (!String.IsNullOrEmpty (project.LocalArchiveName)) {
				string newname = Path.Combine (Guid.NewGuid () + "_" + project.LocalArchiveName.Substring (id_prefix_length));
				File.Move (Path.Combine (DownloadTopdir, "user", user.Name, project.LocalArchiveName), Path.Combine (DownloadTopdir, "user", user.Name, newname));
				project.LocalArchiveName = newname;
			}
			DataStore.RegisterProject (project);
			session.Notification = String.Format ("Registered project '{0}'", project.Name);
			SetSession (ctx, session);
			LoggedHome (ctx, session);
		}

		[Route ("/register/project/edit/{userid}/{project}")]
		public void StartProjectUpdate (IManosContext ctx, string userid, string project)
		{
			AssertLoggedIn (ctx, (c, session) => StartProjectUpdate (c, session, userid, project));
		}
		
		void StartProjectUpdate (IManosContext ctx, DroshSession session, string userid, string project)
		{
			this.RenderSparkView (ctx, "ManageProject.spark", new {Session = session, ManagementMode = ProjectManagementMode.Update, LoggedUser = session.User, Editable = true, Project = DataStore.GetProject (userid, project)});
			ctx.Response.End ();
		}

		[Route ("/register/project/update")]
		public void ExecuteProjectUpdate (IManosContext ctx)
		{
			AssertLoggedIn (ctx, (c, session) => ExecuteProjectUpdate (c, session));
		}

		void ExecuteProjectUpdate (IManosContext ctx, DroshSession session)
		{
			var project = CreateProjectFromForm (session, ctx);
			DataStore.UpdateProject (session.User.Name, project);
			this.RenderSparkView (ctx, "ManageProject.spark", new {Session = session, ManagementMode = ProjectManagementMode.Update, Project = project, LoggedUser = session.User, Editable = true, Notification = "updated!"});
			ctx.Response.End ();
		}

		Project CreateProjectFromForm (DroshSession session, IManosContext ctx)
		{
			var user = session.User.Name;
			var name = ctx.Request.Data ["projectname"];
			var existing = name != null ? DataStore.GetProject (user, name) : null;
			var p = existing != null ? existing.Clone () : new Project () { Id = Guid.NewGuid ().ToString () };
			if (existing != null) {
				p.RegisteredTimestamp = existing.RegisteredTimestamp;
				// FIXME: fill everything else appropriate
			}
			p.Name = name;
			p.Owner = user;
			p.Description = ctx.Request.Data ["description"];
			p.PrimaryLink = ctx.Request.Data ["website"];
			p.Dependencies = (ctx.Request.Data ["deps"] ?? String.Empty).Split (new char [] {' '}, StringSplitOptions.RemoveEmptyEntries);
			p.Builders = (ctx.Request.Data ["builders"] ?? String.Empty).Split (new char [] {' '}, StringSplitOptions.RemoveEmptyEntries);

			// FIXME: this trim should not be required, probably manos issue.
			switch (ctx.Request.Data ["build-type"].Trim ()) {
			case "Prebuilt": p.BuildType = BuildType.Prebuilt; break;
			case "Custom": p.BuildType = BuildType.Custom; break;
			case "NdkBuild": p.BuildType = BuildType.NdkBuild; break;
			case "Autotools": p.BuildType = BuildType.Autotools; break;
			case "CMake": p.BuildType = BuildType.CMake; break;
			default: throw new Exception (String.Format ("Unexpected build type: '{0}'", ctx.Request.Data ["build-type"]));
			}
			switch (ctx.Request.Data ["target-ndk"].Trim ()) {
			case "R5": p.TargetNDKs = NDKType.R5; break;
			case "CrystaxR4": p.TargetNDKs = NDKType.CrystaxR4; break;
			case "R4": p.TargetNDKs = NDKType.R4; break;
			default: throw new Exception (String.Format ("Unexpected target NDK: '{0}'", ctx.Request.Data ["target-ndk"]));
			}

			p.TargetArchs = GetArchTarget (ctx, null, 0);

			// FIXME: handle source-archive

			int n_patch = 0;
			p.Patches = new List<Patch> ();
			while (ctx.Request.Data ["patch-target-" + ++n_patch + "-text"] != null) {
				var patch = new Patch ();
				p.Patches.Add (patch);
			}

			p.Scripts = new List<Script> ();
			p.Scripts.Add (new Script () { Step = ScriptStep.Build, Text = ctx.Request.Data ["script-text-build"] });
			p.Scripts.Add (new Script () { Step = ScriptStep.PreInstall, Text = ctx.Request.Data ["script-text-preinstall"] });
			p.Scripts.Add (new Script () { Step = ScriptStep.Install, Text = ctx.Request.Data ["script-text-install"] });
			p.Scripts.Add (new Script () { Step = ScriptStep.PostInstall, Text = ctx.Request.Data ["script-text-postinstall"] });
/*
			while (ctx.Request.Data ["script-target-" + ++n_script + "-text"] != null) {
				var script = new Script ();
				switch (ctx.Request.Data ["script-step-" + n_script]) {
				case "build": script.Step = ScriptStep.Build; break;
				case "preinstall": script.Step = ScriptStep.PreInstall; break;
				case "install": script.Step = ScriptStep.Install; break;
				case "postinstall": script.Step = ScriptStep.PostInstall; break;
				}
				p.Scripts.Add (script);
			}
*/

			var file = ctx.Request.Files.Get ("source-archive");
			if (file != null) {
				p.PublicArchiveName = file.Name;
				p.LocalArchiveName = SaveFileOnServer (p.Owner, p.Id, file);
			} else {
				p.PublicArchiveName = ctx.Request.Data ["public-archive-name"] ?? p.PublicArchiveName;
				p.LocalArchiveName = ctx.Request.Data ["local-archive-name"] ?? p.LocalArchiveName;
			}

			return p;
		}

		[Route ("/register/project/fork/{srcuser}/{srcproj}/{srcrev}",
			"/register/project/fork/{srcuser}/{srcproj}")]
		public void ForkProject (IManosContext ctx, string srcuser, string srcproj, string srcrev)
		{
			AssertLoggedIn (ctx, (c, session) => ForkProject (c, session, srcuser, srcproj, srcrev));
		}

		void ForkProject (IManosContext ctx, DroshSession session, string srcuser, string srcproj, string srcrev)
		{
			var src = DataStore.GetProject (srcuser, srcproj);
			if (src == null) {
				session.Notification = String.Format ("Project {0}/{1} could not be retrieved.", srcuser, srcproj);
				ShowProjectDetails (ctx, srcuser, srcproj, null, srcrev, session.Notification); // FIXME: use Response.Redirect()
			} else if (src.Owner == session.User.Name) {
				session.Notification = String.Format ("You cannot fork your own project.", srcuser, srcproj);
				ShowProjectDetails (ctx, srcuser, srcproj, null, srcrev, session.Notification); // FIXME: use Response.Redirect()
			} else {
				var user = session.User;
				var fork = src.Clone ();
				fork.Owner = user.Name;
				// make name unique
				if (DataStore.GetProject (fork.Owner, fork.Name) != null) {
					int count = 2;
					while (DataStore.GetProject (fork.Owner, fork.Name + count) != null)
						count++;
					fork.Name += count;
				}
				fork.Id = Guid.NewGuid ().ToString ();
				fork.ForkOrigin = src.Id;
				var newfile = Guid.NewGuid ().ToString () + fork.PublicArchiveName;
				File.Copy (Path.Combine (Drosh.DownloadTopdir, "user", src.Owner, src.LocalArchiveName), Path.Combine (Drosh.DownloadTopdir, "user", fork.Owner, newfile));
				fork.LocalArchiveName = newfile;
				// FIXME: reject null srcrev to avoid complication.
				fork.ForkOriginRevision = srcrev;
				fork.RegisteredTimestamp = DateTime.Now;
				DataStore.RegisterProject (fork);
				session.Notification = "Successfully forked";
				ctx.Response.Redirect ("/register/project/edit/" + fork.Owner + "/" + fork.Name);
			}
		}

		[Route ("/build/show/{buildId}")]
		public void ShowBuild (IManosContext ctx, string buildId)
		{
			var build = DataStore.GetBuild (buildId);
			this.RenderSparkView (ctx, "Build.spark", new { Build = build });
			ctx.Response.End ();
		}

		[Route ("/build/kick/{user}/{project}/{revision}",
			"/build/kick/{user}/{project}")]
		public void KickBuild (IManosContext ctx, string user, string project, string revision)
		{
			AssertLoggedIn (ctx, (c, session) => KickBuild (c, session, user, project, revision));
		}

		void KickBuild (IManosContext ctx, DroshSession session, string user, string project, string revision)
		{
			var proj = DataStore.GetProject (user, project);
foreach (var revv in DataStore.Revisions) Console.WriteLine ("!! {0} {1} {2}", revv.ProjectOwner, revv.ProjectName, revv.RevisionId);
			var rev = DataStore.GetRevision (proj.Owner, proj.Name, revision);
			if (proj == null || rev == null) {
				session.Notification = String.Format ("Project {0}/{1}/{2} could not be retrieved.", user, project, revision);
				ShowProjectDetails (ctx, user, project, null, revision, session.Notification); // FIXME: use Response.Redirect()
			} else if (proj.Owner != session.User.Name && !proj.Builders.Contains (session.User.Name)) {
				session.Notification = String.Format ("You cannot build this project.", user, project);
				ShowProjectDetails (ctx, user, project, null, revision, session.Notification); // FIXME: use Response.Redirect()
			} else {
				var ids = Builder.QueueBuild (rev, session.User.Name);
				foreach (var id in ids) {
					var psi = new ProcessStartInfo () { FileName = "bash", Arguments = Path.Combine (Drosh.BuildServiceToolDir, "run-build") + " " + id, WorkingDirectory = Drosh.BuildServiceToolDir };
					var proc = Process.Start (psi);
					System.Threading.Thread.Sleep (300);
					if (proc.HasExited)
						Console.Error.WriteLine (proc.ExitCode);
				}

				if (proj.Owner == session.User.Name) {
					session.Notification = "A new build has started.";
					ctx.Response.Redirect (String.Format ("/register/project/edit/{0}/{1}", proj.Owner, proj.Name));
				} else {
					session.Notification = "A new build has started.";
					ShowProjectDetails (ctx, user, project, null, revision, session.Notification); // FIXME: use Response.Redirect()
				}
			}
		}

		[Route ("/log/{buildId}")]
		public void ShowLog (IManosContext ctx, string buildId)
		{
			var path = Path.Combine (Drosh.LogTopdir, buildId + ".log");
			if (File.Exists (path))
				ctx.Response.SendFile (path);
			else
				ctx.Response.WriteLine ("log not found for {0}", buildId);
			ctx.Response.End ();
		}

		string SaveFileOnServer (string user, string id, UploadedFile file)
		{
			string uniqueName = id + "_" + file.Name;
			using (var fs = File.Create (Path.Combine (DownloadTopdir, "user", user, uniqueName)))
				file.Contents.CopyTo (fs);
			return uniqueName;
		}

		ArchType GetArchTarget (IManosContext ctx, string prefix, int count)
		{
			var s = ctx.Request.Data [prefix + "target-arch" + (count > 0 ? "-" + count : String.Empty)];
			if (s != null)
				return (ArchType) Enum.Parse (typeof (ArchType), s);
			ArchType ret = ArchType.None;
			if (ctx.Request.Data [prefix + "target-arch-" + (count > 0 ? count + "-" : String.Empty) + "arm"] != null)
				ret |= ArchType.Arm;
			if (ctx.Request.Data [prefix + "target-arch-" + (count > 0 ? count + "-" : String.Empty) + "armV7a"] != null)
				ret |= ArchType.ArmV7a;
			if (ctx.Request.Data [prefix + "target-arch-" + (count > 0 ? count + "-" : String.Empty) + "x86"] != null)
				ret |= ArchType.X86;
			return ret;
		}
	}

	public class Html
	{
		public static string ActionLink (string linkText, string actionName, string controllerName)
		{
			throw new NotImplementedException ();
		}
	}

	public class MailClient
	{
		public static void SendRegistration (User user)
		{
			// FIXME: implement
		}
	}
}


