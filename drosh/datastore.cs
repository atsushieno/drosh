using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using ServiceStack.Redis;

namespace drosh
{
	public class DataStore
	{
		static SHA256 sha256 = SHA256.Create ();

		public static string HashPassword (string rawPassword)
		{
			return Convert.ToBase64String (sha256.ComputeHash (Encoding.UTF8.GetBytes (rawPassword)));
		}

		// so far it does not serialize any data during this development

#if completely_in_memory
		static IList<User> Users = new List<User> ();
		static IList<Project> Projects = new List<Project> ();
		static IList<ProjectSubscription> Subscriptions = new List<ProjectSubscription> ();
		static IList<BuildRecord> Builds = new List<BuildRecord> ();
		static IList<ProjectRevision> Revisions = new List<ProjectRevision> ();
#else
		static RedisClient redis = new RedisClient ();
		static List<object> typed_stores = new List<object> ();
		
		static RedisStore<T> GetStore<T> ()
		{
			object ret = typed_stores.FirstOrDefault (o => o is RedisStore<T>);
			if (ret == null)
				typed_stores.Add ((ret = new RedisStore<T> (redis)));
			return (RedisStore<T>) ret;
		}

		public class RedisStore<T> : IEnumerable<T>
		{
			RedisClient redis;

			public RedisStore (RedisClient redis)
			{
				this.redis = redis;
			}
			
			public IEnumerator<T> GetEnumerator ()
			{
				return redis.GetTypedClient<T> ().GetAll ().GetEnumerator ();
			}
			
			IEnumerator IEnumerable.GetEnumerator ()
			{
				return this.GetEnumerator ();
			}
			
			public void Add (T obj)
			{
				redis.GetTypedClient<T> ().Store (obj);
			}

			public void Remove (T obj)
			{
				redis.GetTypedClient<T> ().Delete (obj);
			}
		}

		public static RedisStore<User> Users {
			get { return GetStore<User> (); }
		}
		public static RedisStore<Project> Projects {
			get { return GetStore<Project> (); }
		}
		public static RedisStore<ProjectSubscription> Subscriptions {
			get { return GetStore<ProjectSubscription> (); }
		}
		public static RedisStore<BuildRecord> Builds {
			get { return GetStore<BuildRecord> (); }
		}
		public static RedisStore<ProjectRevision> Revisions {
			get { return GetStore<ProjectRevision> (); }
		}
#endif

		public static void RegisterUser (User user)
		{
			if (Users.Any (u => u.Name == user.Name))
				throw new Exception ("duplicate user name");
			Users.Add (user);
		}

		public static User GetUser (string name)
		{
			return Users.FirstOrDefault (u => u.Name == name);
		}

		public static void Update (User user)
		{
			if (!Users.Any (u => u.Name == user.Name))
				throw new Exception ("The user does not exist");
			Users.Remove (GetUser (user.Name));
			Users.Add (user);
		}

		public static void UpdateUser (User user)
		{
			if (!Users.Any (u => u.Name == user.Name))
				throw new Exception ("The user does not exist");
			Users.Remove (GetUser (user.Name));
			Users.Add (user);
		}

		public static void DeleteUser (string name)
		{
			var user = GetUser (name);
			if (user != null)
				Users.Remove (user);
		}

		public static void RegisterProject (string user, Project project)
		{
			if (Projects.Any (p => p.Owner == user && p.Name == project.Name))
				throw new Exception ("duplicate project name");
			Projects.Add (project);
		}

		public static Project GetProject (string user, string name)
		{
			return Projects.FirstOrDefault (p => p.Owner == user && p.Name == name);
		}

		public static Project GetProject (string id)
		{
			return Projects.FirstOrDefault (p => p.Id == id);
		}

		public static void UpdateProject (string user, Project project)
		{
			if (!Projects.Any (p => p.Owner == user && p.Name == project.Name))
				throw new Exception ("The project does not exist");
			Projects.Remove (GetProject (user, project.Name));
			Projects.Add (project);
		}

		public static IEnumerable<Project> GetProjectsByUser (string user)
		{
			return Projects.Where (p => p.Owner == user || Subscriptions.Any (s => s.Project == p.Id && s.User == user));
		}

		public static IEnumerable<BuildRecord> GetLatestBuildsByUser (string user, int skip, int take)
		{
			return Builds.Where (b => b.Builder == user).OrderBy (b => b.BuildStartedTimestamp).Skip (skip).Take (take);
		}

		public static IEnumerable<BuildRecord> GetLatestBuildsByProject (string projectId, int skip, int take)
		{
			return Builds.Where (b => b.Project == projectId).OrderBy (b => b.BuildStartedTimestamp).Skip (skip).Take (take);
		}

		public static IEnumerable<ProjectRevision> GetRevisions (string userid, string projectname, int skip, int take)
		{
			return Revisions.Where (r => r.Owner == userid && r.Project == projectname).OrderBy (r => r.CreatedTimestamp).Skip (skip).Take (take);
		}
	}
}
