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
			object ret = null;
			Cache.Get (key, delegate (string k, object value) { ret = value; });
			return (DroshSession) ret;
		}

		void AssertLoggedIn (IManosContext ctx, Action<IManosContext,DroshSession> action)
		{
			var session = GetSessionCache (ctx.Request.Data ["session"]);
			if (session == null || session.User == null)
				Index (ctx, "Login status expired or not logged in");
			else
				action (ctx, session);
		}
		
		[Route ("/", "/index")]
		public void Index (IManosContext ctx, string notification)
		{
			this.RenderSparkView (ctx, "Index.spark", new { Notification = notification});
			ctx.Response.End ();
		}

		[Route ("/login")]
		public void Login (IManosContext ctx, string userid, string password, string link)
		{
			var user = DataStore.GetUser (userid);
			if (user == null || user.PasswordHash != DataStore.HashPassword (password))
				Index (ctx, "Wrong user name or password");
			string sessionId = Guid.NewGuid ().ToString ();
			var session = new DroshSession (sessionId, user);
			Cache.Set (sessionId, session, TimeSpan.FromMinutes (60));
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

		User GetLoggedUser (IManosContext ctx)
		{
			var sessionId = ctx.Request.Data ["session"];
			if (sessionId != null) {
				var session = GetSessionCache (sessionId);
				return session.User;
			}
			else
				return null;
		}
		
		[Route ("/register/user/new")]
		public void StartUserRegistration (IManosContext ctx)
		{
			this.RenderSparkView (ctx, "ManageUser.spark", new {ManagementMode = UserManagementMode.New, Editable = true});
			ctx.Response.End ();
		}

		[Route ("/register/user/confirm")]
		public void ConfirmUserRegistration (IManosContext ctx)
		{
			this.RenderSparkView (ctx, "ManageUser.spark", new {ManagementMode = UserManagementMode.Confirm, User = CreateUserFromForm (ctx), Editable = false});
			ctx.Response.End ();
		}

		[Route ("/register/user/register")]
		void ExecuteUserRegistration (IManosContext ctx)
		{
			var user = CreateUserFromForm (ctx);
			DataStore.RegisterUser (user);
#if MAIL
			MailClient.SendRegistration (user);
			Index (ctx, "Confirmation email will be sent to your inbox");
#else
			string sessionId = Guid.NewGuid ().ToString ();
			var session = new DroshSession (sessionId, user);
			Cache.Set (sessionId, session, TimeSpan.FromMinutes (60));
			LoggedHome (ctx, session, "You are now registered");
#endif
		}

		[Route ("/register/user/edit")]
		public void StartUserUpdate (IManosContext ctx, DroshSession session)
		{
			this.RenderSparkView (ctx, "ManageUser.spark", new {ManagementMode = UserManagementMode.Update, User = session.User, Editable = true});
			ctx.Response.End ();
		}

		[Route ("/register/user/update")]
		public void ExecuteUserUpdate (IManosContext ctx, DroshSession session)
		{
			var user = CreateUserFromForm (ctx);
			// process password change request with care: check existing password
			var rawpwd = ctx.Request.Data ["old-password"];
			if (ctx.Request.Data ["password"] != null && ctx.Request.Data ["password"] != ctx.Request.Data ["password-verified"] || rawpwd != null && DataStore.HashPassword (rawpwd) != user.PasswordHash) {
				this.RenderSparkView (ctx, "ManageUser.spark", new {ManagementMode = UserManagementMode.Update, User = user, Editable = true, Notification = "Password didn't match"});
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

		[Route ("/home")]
		public void Home (IManosContext ctx, string notification)
		{
			AssertLoggedIn (ctx, (c, session) => LoggedHome (c, session, notification));
		}

		void LoggedHome (IManosContext ctx, DroshSession session, string notification)
		{
			this.RenderSparkView (ctx, "Home.spark", new { LoggedUser = session.User, Notification = notification});
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
	}

	public class MailClient
	{
		public static void SendRegistration (User user)
		{
			// FIXME: implement
		}
	}
}
