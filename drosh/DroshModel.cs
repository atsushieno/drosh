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
	
	public enum SubscriptionKind
	{
		BuilderPending,
		Builder,
		Watcher,
		Suspended
	}
	
	public class PerTargetResource
	{
		public NDKType TargetNDKs { get; set; }
		public ArchType TargetArchs { get; set; }
		public string Text { get; set; }
	}
	
	public class Project
	{
		public string Name { get; set; }
		public string Description { get; set; }
		public string PrimaryLink { get; set; }
		public UserReference Owner { get; set; }
		public IList<ProjectReference> Dependencies { get; set; }
		public BuildType BuildType { get; set; }
		public string SourceArchiveName { get; set; }
		public IList<PerTargetResource> Patches { get; set; }
		public IList<PerTargetResource> Scripts { get; set; }
		public NDKType TargetNDKs { get; set; }
		public ArchType TargetArchs { get; set; }
		public IList<string> FilesByPackage { get; set; }
		public DateTime CreatedTimestamp { get; set; }
		public DateTime LastUpdatedTimestamp { get; set; }
	}

	public class ProjectRevision
	{
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
		public ProjectRevisionReference ProjectRevision { get; set; }
		public NDKType TargetNdk { get; set; }
		public ArchType TargetArch { get; set; }
		public UserReference Builder { get; set; }
		public DateTime BuildStartedTimestamp { get; set; }
		public BuildStatus Status { get; set; }
		public string Log { get; set; }
	}
}

