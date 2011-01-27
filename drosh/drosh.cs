using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using Manos;
using Manos.Http;
using Manos.Spark;

namespace drosh
{
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
	}

	public class drosh : ManosApp
	{
		public drosh ()
		{
			Route ("/default.css", new StaticContentModule ());
			Route ("/images/", new StaticContentModule ());
		}

		DroshSession GetSession (IManosContext ctx)
		{
			var sessionId = ctx.Request.Cookies.Get ("drosh-session");
Console.Error.WriteLine ("get cookie : " + sessionId);
			return sessionId != null ? GetSessionCache (sessionId) : null;
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
			var session = GetSession (ctx);
			if (session == null || session.User == null)
				Index (ctx, "Login status expired or not logged in");
			else {
				ctx.Response.SetCookie ("drosh-session", new HttpCookie ("drosh-session", session.Id) {Path = "/"/*, Expires = DateTime.Now.AddMinutes (60)*/});
Console.Error.WriteLine ("set-cookie: " + session.Id);
				action (ctx, session);
			}
		}
		
		[Route ("/", "/index", "/home")]
		public void Index (IManosContext ctx, string notification)
		{
			var session = GetSession (ctx);
			if (session == null || session.User == null)
				NotLogged (ctx, notification);
			else
				LoggedHome (ctx, session, notification);
		}

		void NotLogged (IManosContext ctx, string notification)
		{
			this.RenderSparkView (ctx, "Index.spark", new { Notification = notification});
			ctx.Response.End ();
		}

		[Route ("/login")]
		public void Login (IManosContext ctx, string link)
		{
			var userid = ctx.Request.Data ["login_user"];
			var passraw = ctx.Request.Data ["login_password"];
			var user = DataStore.GetUser (userid);
			if (user == null || passraw == null || user.PasswordHash != DataStore.HashPassword (passraw)) {
				Index (ctx, String.Format ("User name '{0}' does not exist or password is wrong", userid));
				return;
			}
			string sessionId = Guid.NewGuid ().ToString ();
			var session = new DroshSession (sessionId, user);
			Cache.Set (sessionId, session, TimeSpan.FromMinutes (60));
			ctx.Response.SetCookie ("drosh-session", new HttpCookie ("drosh-session", sessionId) {Path = "/"/*, Expires = DateTime.Now.AddMinutes (60)*/});
Console.Error.WriteLine ("set-cookie: " + sessionId);

			if (link != null)
				ctx.Response.Redirect (link);
			else
				LoggedHome (ctx, session, String.Format ("Welcome, {0}!", user.Name));
		}

		[Route ("/logout")]
		public void Logout (IManosContext ctx)
		{
			var session = ctx.Request.Data ["session"];
			if (session != null) {
				Cache.Remove (session);
				Index (ctx, "logged out");
			}
			else
				Index (ctx, "not logged in");
		}

		// User registration

		[Route ("/register/user/new")]
		public void StartUserRegistration (IManosContext ctx, string notification)
		{
			this.RenderSparkView (ctx, "ManageUser.spark", new {ManagementMode = UserManagementMode.New, Editable = true, Notification = notification, User = CreateUserFromForm (ctx)});
			ctx.Response.End ();
		}

		[Route ("/register/user/confirm")]
		public void ConfirmUserRegistration (IManosContext ctx)
		{
			// FIXME: validate inputs more.
			var pwd = ctx.Request.Data ["password"];
			var pwdv = ctx.Request.Data ["password-verified"];
			if (pwd != pwdv)
				StartUserRegistration (ctx, "Password inputs don't match. [" + pwd + "][" + pwdv + "]");
			else if (DataStore.GetUser (ctx.Request.Data ["username"]) != null)
				StartUserRegistration (ctx, "The same user name is already registered. Please pick another name.");
			else {
				this.RenderSparkView (ctx, "ManageUser.spark", new {ManagementMode = UserManagementMode.Confirm, User = CreateUserFromForm (ctx), Editable = false});
				ctx.Response.End ();
			}
		}

		[Route ("/register/user/register")]
		public void ExecuteUserRegistration (IManosContext ctx)
		{
			// FIXME: validate inputs.

			var user = CreateUserFromForm (ctx);
			DataStore.RegisterUser (user);
#if MAIL
			MailClient.SendRegistration (user);
			Index (ctx, "Confirmation email will be sent to your inbox");
#else
			string sessionId = Guid.NewGuid ().ToString ();
			var session = new DroshSession (sessionId, user);
			Cache.Set (sessionId, session, TimeSpan.FromMinutes (60));
			ctx.Response.SetCookie ("drosh-session", new HttpCookie ("drosh-session", sessionId) {Path = "/"/*, Expires = DateTime.Now.AddMinutes (60)*/});
Console.Error.WriteLine ("set-cookie: " + sessionId);

			LoggedHome (ctx, session, "You are now registered");
#endif
		}

		[Route ("/register/user/edit")]
		public void StartUserUpdate (IManosContext ctx, DroshSession session)
		{
			this.RenderSparkView (ctx, "ManageUser.spark", new {Session = session, ManagementMode = UserManagementMode.Update, User = session.User, Editable = true});
			ctx.Response.End ();
		}

		[Route ("/register/user/update")]
		public void ExecuteUserUpdate (IManosContext ctx, DroshSession session)
		{
			var user = CreateUserFromForm (ctx);
			// process password change request with care: check existing password
			var rawpwd = ctx.Request.Data ["old-password"];
			if (ctx.Request.Data ["password"] != null && ctx.Request.Data ["password"] != ctx.Request.Data ["password-verified"] || rawpwd != null && DataStore.HashPassword (rawpwd) != user.PasswordHash) {
				this.RenderSparkView (ctx, "ManageUser.spark", new {Session = session, ManagementMode = UserManagementMode.Update, User = user, Editable = true, Notification = "Password didn't match"});
			} else {
				user.PasswordHash = DataStore.HashPassword (ctx.Request.Data ["password"]) ?? user.PasswordHash;
				DataStore.Update (user);
				this.RenderSparkView (ctx, "ManageUser.spark", new {ManagementMode = UserManagementMode.Update, User = user, Editable = true, Notification = "updated!"});
			}
			ctx.Response.End ();
		}

		[Route ("/register/user/recovery")]
		public void StartPasswordRecovery (IManosContext ctx, string notification)
		{
			this.RenderSparkView (ctx, "PasswordRecovery.spark", new {Notification = notification});
			ctx.Response.End ();
		}

		[Route ("/register/user/recovery/execute")]
		public void ExecutePasswordRecovery (IManosContext ctx)
		{
			var name = ctx.Request.Data ["userid"];
			var user = DataStore.GetUser (name);
			if (user == null)
				StartPasswordRecovery (ctx, String.Format ("User '{0}' was not found", name));
			else
				Index (ctx, "Your account is temporarily suspended and verification email is sent to you");
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
			u.Name = name;
			u.OpenID = ctx.Request.Data ["openid"];
			u.Profile = ctx.Request.Data ["profile"];

			return u;
		}

		// Logged

		void LoggedHome (IManosContext ctx, DroshSession session, string notification)
		{
			this.RenderSparkView (ctx, "Home.spark", new { Session = session, LoggedUser = session.User, Notification = notification, Builds = DataStore.GetLatestBuildsByUser (session.User.Name, 0, 10), Projects = DataStore.GetProjectsByUser (session.User.Name) });
			ctx.Response.End ();
		}
		
		// Project Browse

		[Route ("/project/{userid}/{projectname}/{revision}", "/project/{userid}/{projectname}")]
		public void ProjectDetails (IManosContext ctx, string userid, string projectname, string projectId, string revision, string notification)
		{
			var session = GetSession (ctx);
			var project = projectname != null ? DataStore.GetProject (userid, projectname) : DataStore.GetProject (projectId);
			if (project == null)
				Index (ctx, String.Format ("Project '{0}' was not found. Make sure that the link is correct.", projectId ?? userid + "/" + projectname));
			else {
				var builds = DataStore.GetLatestBuildsByProject (project.Id, 0, 10);
				var revs = DataStore.GetRevisions (userid, projectname, 0, 10);
				this.RenderSparkView (ctx, "Project.spark", new { Session = session, LoggedUser = session == null ? null : session.User, Notification = notification, Project = project, Builds = builds, Revisions = revs});

				ctx.Response.End ();
			}
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
			DataStore.RegisterProject (user.Name, project);
			LoggedHome (ctx, session, String.Format ("Registered project '{0}'", project.Name));
		}

		[Route ("/register/project/edit")]
		public void StartProjectUpdate (IManosContext ctx)
		{
			AssertLoggedIn (ctx, (c, session) => StartProjectUpdate (c, session));
		}
		
		void StartProjectUpdate (IManosContext ctx, DroshSession session)
		{
			this.RenderSparkView (ctx, "ManageProject.spark", new {Session = session, ManagementMode = ProjectManagementMode.Update, LoggedUser = session.User, Editable = true, Project = CreateProjectFromForm (session, ctx)});
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

			switch (ctx.Request.Data ["build-type"]) {
			case "prebuilt": p.BuildType = BuildType.Prebuilt; break;
			case "custom": p.BuildType = BuildType.Custom; break;
			case "ndk-build": p.BuildType = BuildType.NdkBuild; break;
			case "autotools": p.BuildType = BuildType.Autotools; break;
			case "cmake": p.BuildType = BuildType.CMake; break;
			default: throw new Exception (ctx.Request.Data ["build-type"]);
			}

			p.TargetNDKs = GetNDKTarget (ctx, null, 0);
			p.TargetArchs = GetArchTarget (ctx, null, 0);

			// FIXME: handle source-archive

			int n_patch = 0;
			p.Patches = new List<Patch> ();
			while (ctx.Request.Data ["patch-target-" + ++n_patch + "-text"] != null) {
				var patch = new Patch ();
				patch.TargetNDKs = GetNDKTarget (ctx, "patch-", n_patch);
				patch.TargetArchs = GetArchTarget (ctx, "patch-", n_patch);
				p.Patches.Add (patch);
			}

			int n_script = 0;
			p.Scripts = new List<Script> ();
			while (ctx.Request.Data ["script-target-" + ++n_script + "-text"] != null) {
				var script = new Script ();
				switch (ctx.Request.Data ["script-step-" + n_script]) {
				case "build": script.Step = ScriptStep.Build; break;
				case "preinstall": script.Step = ScriptStep.PreInstall; break;
				case "install": script.Step = ScriptStep.Install; break;
				case "postinstall": script.Step = ScriptStep.PostInstall; break;
				}
				script.TargetNDKs = GetNDKTarget (ctx, "script-", n_patch);
				script.TargetArchs = GetArchTarget (ctx, "script-", n_patch);
				p.Scripts.Add (script);
			}

			// FIXME: fill everything else appropriate

			return p;
		}

		NDKType GetNDKTarget (IManosContext ctx, string prefix, int count)
		{
			NDKType ret = NDKType.None;
			if (ctx.Request.Data [prefix + "target-ndk-" + (count > 0 ? count + "-" : String.Empty) + "r5"] != null)
				ret |= NDKType.R5;
			if (ctx.Request.Data [prefix + "target-ndk-" + (count > 0 ? count + "-" : String.Empty) + "crystaxR4b"] != null)
				ret |= NDKType.CrystaxR4b;
			if (ctx.Request.Data [prefix + "target-ndk-" + (count > 0 ? count + "-" : String.Empty) + "r4b"] != null)
				ret |= NDKType.R4b;
			return ret;
		}

		ArchType GetArchTarget (IManosContext ctx, string prefix, int count)
		{
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

	public class DataStore
	{
		static SHA256 sha256 = SHA256.Create ();

		public static string HashPassword (string rawPassword)
		{
			return Convert.ToBase64String (sha256.ComputeHash (Encoding.UTF8.GetBytes (rawPassword)));
		}

		// so far it does not serialize any data during this development

		static List<User> users = new List<User> ();
		static List<Project> projects = new List<Project> ();
		static List<ProjectSubscription> subscriptions = new List<ProjectSubscription> ();
		static List<BuildRecord> builds = new List<BuildRecord> ();
		static List<ProjectRevision> revisions = new List<ProjectRevision> ();

		public static void RegisterUser (User user)
		{
			if (users.Any (u => u.Name == user.Name))
				throw new Exception ("duplicate user name");
			users.Add (user);
		}

		public static User GetUser (string name)
		{
			return users.FirstOrDefault (u => u.Name == name);
		}

		public static void Update (User user)
		{
			if (!users.Any (u => u.Name == user.Name))
				throw new Exception ("The user does not exist");
			users.Remove (GetUser (user.Name));
			users.Add (user);
		}

		public static void UpdateUser (User user)
		{
			if (!users.Any (u => u.Name == user.Name))
				throw new Exception ("The user does not exist");
			users.Remove (GetUser (user.Name));
			users.Add (user);
		}

		public static void RegisterProject (string user, Project project)
		{
			if (projects.Any (p => p.Owner == user && p.Name == project.Name))
				throw new Exception ("duplicate project name");
			projects.Add (project);
		}

		public static Project GetProject (string user, string name)
		{
			return projects.FirstOrDefault (p => p.Owner == user && p.Name == name);
		}

		public static Project GetProject (string id)
		{
			return projects.FirstOrDefault (p => p.Id == id);
		}

		public static void UpdateProject (string user, Project project)
		{
			if (!projects.Any (p => p.Owner == user && p.Name == project.Name))
				throw new Exception ("The project does not exist");
			projects.Remove (GetProject (user, project.Name));
			projects.Add (project);
		}

		public static IEnumerable<Project> GetProjectsByUser (string user)
		{
			return projects.Where (p => p.Owner == user || subscriptions.Any (s => s.Project == p.Id && s.User == user));
		}

		public static IEnumerable<BuildRecord> GetLatestBuildsByUser (string user, int skip, int take)
		{
			return builds.Where (b => b.Builder == user).OrderBy (b => b.BuildStartedTimestamp).Skip (skip).Take (take);
		}

		public static IEnumerable<BuildRecord> GetLatestBuildsByProject (string projectId, int skip, int take)
		{
			return builds.Where (b => b.Project == projectId).OrderBy (b => b.BuildStartedTimestamp).Skip (skip).Take (take);
		}

		public static IEnumerable<ProjectRevision> GetRevisions (string userid, string projectname, int skip, int take)
		{
			return revisions.Where (r => r.Owner == userid && r.Project == projectname).OrderBy (r => r.CreatedTimestamp).Skip (skip).Take (take);
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

