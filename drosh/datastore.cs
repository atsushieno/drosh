using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using ServiceStack.Redis;

using ProjectReference = System.String; // Project.Id
using RevisionReference = System.String; // Revision.RevisionId

namespace drosh
{
	public class Drosh
	{
		static readonly string appbase = Directory.GetParent (typeof (Drosh).Assembly.Location).FullName;
		public static readonly string DownloadTopdir, BuildTopdir, AndroidNdkR5, AndroidNdkR4, AndroidNdkCrystaxR4;

		static Drosh ()
		{
			DownloadTopdir = Path.Combine (appbase, "pub");
			BuildTopdir = Path.Combine (appbase, "builds");
			AndroidNdkR5 = Path.Combine (appbase, "ndk-r5");
			AndroidNdkCrystaxR4 = Path.Combine (appbase, "ndk-crystax-r4");
			AndroidNdkR4 = Path.Combine (appbase, "ndk-r4");
		}

		public static string GetAndroidRoot (NDKType type)
		{
			switch (type) {
			case NDKType.R5:
				return AndroidNdkR5;
			case NDKType.CrystaxR4b:
				return AndroidNdkCrystaxR4;
			case NDKType.R4b:
				return AndroidNdkR4;
			default:
				throw new ArgumentOutOfRangeException ("type");
			}
		}
	}

	public class DataStore
	{
		static DataStore ()
		{
			// hack any maintenance code here.
		}

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
			return Users.FirstOrDefault (u => u.Name == name && u.Status == UserStatus.Active);
		}

		public static User GetUserByVerificationCode (string name, string verification)
		{
			return Users.FirstOrDefault (u => u.Name == name && u.Verification == verification && u.Status != UserStatus.Removed);
		}

		public static void UpdateUser (User user)
		{
			if (!Users.Any (u => u.Name == user.Name))
				throw new Exception ("The user does not exist");
			Users.Remove (Users.First (u => u.Name == user.Name));
			Users.Add (user);
		}

		public static void DeleteUser (string name)
		{
			var user = GetUser (name);
			if (user != null)
				Users.Remove (user);
		}

		public static void RegisterProject (Project project)
		{
			if (Projects.Any (p => p.Owner == project.Owner && p.Name == project.Name))
				throw new Exception ("duplicate project name");
			Projects.Add (project);
			AddRevisionForProject (project);
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
			AddRevisionForProject (project);
		}

		static void AddRevisionForProject (Project project)
		{
			var rev= new ProjectRevision () { ProjectOwner = project.Owner, ProjectName = project.Name, RevisionId = Guid.NewGuid ().ToString (), CreatedTimestamp = project.LastUpdatedTimestamp };
			Revisions.Add (rev);
		}

		public static IEnumerable<Project> GetProjectsByUser (string user)
		{
			return Projects.Where (p => p.Owner == user || Subscriptions.Any (s => s.Project == p.Id && s.User == user));
		}

		public static IEnumerable<BuildRecord> GetLatestBuildsByUser (string user, int skip, int take)
		{
			return Builds.Where (b => b.Builder == user).OrderBy (b => b.BuildStartedTimestamp).Skip (skip).Take (take);
		}

		public static IEnumerable<BuildRecord> GetLatestBuildsByProject (string projectOwner, string projectName, int skip, int take)
		{
			return Builds.Where (b => b.ProjectOwner == projectOwner && b.ProjectName == projectName).OrderBy (b => b.BuildStartedTimestamp).Skip (skip).Take (take);
		}

		public static ProjectRevision GetRevision (string owner, string projectName, string revision)
		{
			revision = revision == "Head" ? null : revision;
			return Revisions.OrderByDescending (r => r.CreatedTimestamp).FirstOrDefault (r => r.ProjectOwner == owner && r.ProjectName == projectName && (revision == null || r.RevisionId == revision));
		}

		public static IEnumerable<Project> GetProjectsByKeyword (string keyword, int skip, int take)
		{
			return Projects.Where (p => p.Name.Contains (keyword) || p.Owner.Contains (keyword)).Skip (skip).Take (take);
		}

		// Revisions related

		public static IEnumerable<ProjectRevision> GetRevisions (string owner, string projectName, int skip, int take)
		{
			return Revisions.Where (r => r.ProjectOwner == owner && r.ProjectName == projectName).OrderBy (r => r.CreatedTimestamp).Skip (skip).Take (take);
		}

		public static void RegisterRevision (ProjectRevision revision)
		{
			// FIXME: git commit here?
			Revisions.Add (revision);
		}

		public static Project GetProjectWithRevision (string owner, string project, RevisionReference revision)
		{
			// FIXME: make use of revision parameter
			return GetProject (owner, project);
		}

		// Build related

		public static BuildRecord GetBuild (string buildId)
		{
			return Builds.First (b => b.BuildId == buildId);
		}

		public static BuildRecord GetLatestBuild (Project project, ArchType arch)
		{
			return (from b in Builds where b.ProjectOwner == project.Owner && b.ProjectName == project.Name && b.TargetArch == arch && b.Status == BuildStatus.Success orderby b.BuildStartedTimestamp select b).FirstOrDefault ();
		}

		public static void RegisterBuildRecord (BuildRecord build)
		{
			Builds.Add (build);
		}

		public static void UpdateBuildRecord (BuildRecord build)
		{
			Builds.Remove (GetBuild (build.BuildId));
			Builds.Add (build);
		}
	}
}
