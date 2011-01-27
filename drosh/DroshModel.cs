using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

using UserReference = System.String;
using ProjectReference = System.String;
using ProjectRevisionReference = System.String;

namespace drosh
{
	[Flags]
	public enum NDKType
	{
		None = 0,
		R5 = 1,
		CrystaxR4b = 2,
		R4b = 4
	}
	
	[Flags]
	public enum ArchType
	{
		None = 0,
		Arm = 1,
		ArmV7a = 2,
		X86 = 4
	}
	
	public enum BuildType
	{
		Prebuilt,
		Custom,
		NdkBuild,
		Autotools,
		CMake // future
	}
	
	public enum ScriptStep
	{
		Build,
		PreInstall,
		Install,
		PostInstall
	}
	
	public enum SubscriptionKind
	{
		BuilderPending,
		Builder,
		Watcher,
		Suspended
	}
	
	public class Patch
	{
		public NDKType TargetNDKs { get; set; }
		public ArchType TargetArchs { get; set; }
		public string Text { get; set; }

		public Patch Clone ()
		{
			return (Patch) MemberwiseClone ();
		}
	}
	
	public class Script
	{
		public ScriptStep Step { get; set; }
		public NDKType TargetNDKs { get; set; }
		public ArchType TargetArchs { get; set; }
		public string Text { get; set; }

		public Script Clone ()
		{
			return (Script) MemberwiseClone ();
		}
	}

	public class Project
	{
		public string Id { get; set; } // FIXME: it should replace usage of Name as identity (Name is not unique without Owner).
		public string Name { get; set; }
		public string Description { get; set; }
		public string PrimaryLink { get; set; }
		public UserReference Owner { get; set; }
		public IList<ProjectReference> Dependencies { get; set; }
		public IList<UserReference> Builders { get; set; }
		public BuildType BuildType { get; set; }
		public string SourceArchiveName { get; set; }
		public IList<Patch> Patches { get; set; }
		public IList<Script> Scripts { get; set; }
		public NDKType TargetNDKs { get; set; }
		public ArchType TargetArchs { get; set; }
		public IList<string> FilesByPackage { get; set; }
		public DateTime RegisteredTimestamp { get; set; }
		public DateTime LastUpdatedTimestamp { get; set; }

		public string ResultArchiveName {
			get { return Name + "-bin.tar.bz2"; }
		}

		public Project Clone ()
		{
			var ret = (Project) MemberwiseClone ();
			if (Dependencies != null)
				ret.Dependencies = new List<ProjectReference> (Dependencies);
			if (Builders != null)
				ret.Builders = new List<UserReference> (Builders);
			if (Patches != null)
				ret.Patches = new List<Patch> ((from p in Patches select p.Clone ()));
			if (Scripts != null)
				ret.Scripts = new List<Script> ((from s in Scripts select s.Clone ()));
			if (FilesByPackage != null)
				ret.FilesByPackage = new List<string> (FilesByPackage);
			return ret;
		}
	}

	public class ProjectRevision
	{
		public UserReference Owner { get; set; }
		public ProjectReference Project { get; set; }
		public string RevisionId { get; set; }
		public DateTime CreatedTimestamp { get; set; }
		public string OutputArchiveName { get; set; }
	}

	public class ProjectSubscription // project builder users
	{
		public ProjectReference Project { get; set; }
		public UserReference User { get; set; }
		public SubscriptionKind SubscriptionKind { get; set; }
	}

	public class User
	{
		public string Name { get; set; }
		public string OpenID { get; set; }
		public string Email { get; set; }
		public string PasswordHash { get; set; }
		public UserStatus Status { get; set; }
		public DateTime RegisteredTimestamp { get; set; }
		public string Profile { get; set; }
	}
	
	public enum UserStatus
	{
		Pending,
		Active,
		Inactive, // useful?
		Removed
	}
	
	public enum BuildStatus
	{
		Queued,
		Ongoing,
		Success,
		Failure
	}

	public class BuildRecord
	{
		public string BuildId { get; set; }
		public ProjectReference Project { get; set; }
		public ProjectRevisionReference ProjectRevision { get; set; }
		public NDKType TargetNDK { get; set; }
		public ArchType TargetArch { get; set; }
		public UserReference Builder { get; set; }
		public DateTime BuildStartedTimestamp { get; set; }
		public BuildStatus Status { get; set; }
		public string Log { get; set; }
	}
}

