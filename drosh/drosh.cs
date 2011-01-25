using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using Manos;
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
			var sessionId = ctx.Request.Cookies.Get ("session");
Console.Error.WriteLine ("get cookie : " + sessionId);
			var session = GetSessionCache (sessionId);
			if (session == null || session.User == null)
				Index (ctx, "Login status expired or not logged in");
			else {
				ctx.Response.SetCookie ("session", sessionId, DateTime.Now.AddMinutes (60));
				action (ctx, session);
			}
		}
		
		[Route ("/", "/index", "/home")]
		public void Index (IManosContext ctx, string notification)
		{
			var sessionId = ctx.Request.Cookies.Get ("session");
Console.Error.WriteLine ("get cookie : " + sessionId);
			var session = GetSessionCache (sessionId);
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
			var userid = ctx.Request.Data ["userid"];
			var passraw = ctx.Request.Data ["password"];
			var user = DataStore.GetUser (userid);
			if (user == null || user.PasswordHash != DataStore.HashPassword (passraw)) {
				Index (ctx, "Wrong user name or password");
				return;
			}
			string sessionId = Guid.NewGuid ().ToString ();
			var session = new DroshSession (sessionId, user);
			Cache.Set (sessionId, session, TimeSpan.FromMinutes (60));
			ctx.Response.SetCookie ("session", sessionId, "www19337u.sakura.ne.jp", TimeSpan.FromMinutes (60));
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
			this.RenderSparkView (ctx, "ManageUser.spark", new {ManagementMode = UserManagementMode.New, Editable = true, Notification = notification});
			ctx.Response.End ();
		}

		[Route ("/register/user/confirm")]
		public void ConfirmUserRegistration (IManosContext ctx)
		{
			// FIXME: validate inputs more.
			if (ctx.Request.Data ["password"] != ctx.Request.Data ["password-verified"])
				StartUserRegistration (ctx, "Password inputs don't match.");
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
			ctx.Response.SetCookie ("session", sessionId, "www19337u.sakura.ne.jp", TimeSpan.FromMinutes (60));
Console.Error.WriteLine ("set cookie : " + sessionId);

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

		[Route ("/register/user/recovery")]
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
			this.RenderSparkView (ctx, "Home.spark", new { Session = session, LoggedUser = session.User, Notification = notification, Builds = DataStore.GetLatestBuildsByUser (session.User.Name, 0), Projects = DataStore.GetProjectsByUser (session.User.Name) });
			ctx.Response.End ();
		}
		
		// Projects
		
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
			this.RenderSparkView (ctx, "ManageProject.spark", new {Session = session, ManagementMode = ProjectManagementMode.Confirm, LoggedUser = session.User, Editable = false});
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
		public void StartProjectUpdate (IManosContext ctx, DroshSession session)
		{
			this.RenderSparkView (ctx, "ManageProject.spark", new {Session = session, ManagementMode = ProjectManagementMode.Update, LoggedUser = session.User, Editable = true, Project = CreateProjectFromForm (session, ctx)});
			ctx.Response.End ();
		}

		[Route ("/register/project/update")]
		public void ExecuteProjectUpdate (IManosContext ctx, DroshSession session)
		{
			var project = CreateProjectFromForm (session, ctx);
			DataStore.UpdateProject (session.User.Name, project);
			this.RenderSparkView (ctx, "ManageProject.spark", new {Session = session, ManagementMode = ProjectManagementMode.Update, Project = project, LoggedUser = session.User, Editable = true, Notification = "updated!"});
			ctx.Response.End ();
		}

		Project CreateProjectFromForm (DroshSession session, IManosContext ctx)
		{
			var user = session.User.Name;
			var name = ctx.Request.Data ["project"];
			var p = new Project ();
			var existing = name != null ? DataStore.GetProject (user, name) : null;
			if (existing != null) {
				p.RegisteredTimestamp = existing.RegisteredTimestamp;
				// FIXME: fill everything else appropriated
			}
			p.Name = name;
			p.Description = ctx.Request.Data ["description"];
			p.PrimaryLink = ctx.Request.Data ["website"];
			p.Owner = user;

			// FIXME: fill everything else appropriated

			return p;
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

		public static IEnumerable<BuildRecord> GetLatestBuildsByUser (string user, int skip)
		{
			return builds.OrderBy (b => b.BuildStartedTimestamp).Skip (skip).Take (10);
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
