using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;

using ProjectReference = System.String; // Project.Id
using RevisionReference = System.String; // Revision.RevisionId

namespace drosh
{
	public class Drosh
	{
		static readonly string appbase = Directory.GetParent (typeof (Drosh).Assembly.Location).FullName;
		public static readonly string ToolDir, BuildServiceToolDir,
			DownloadTopdir, BuildTopdir, LogTopdir, ScriptsTopdir,
			AndroidNdkR5, AndroidNdkR4, AndroidNdkCrystaxR4;

		static Drosh ()
		{
			ToolDir = appbase;
			BuildServiceToolDir = Path.GetFullPath (Path.Combine (appbase, "..", "build-service"));
			DownloadTopdir = Path.Combine (appbase, "pub");
			BuildTopdir = Path.Combine (appbase, "builds");
			LogTopdir = Path.Combine (appbase, "logs");
			ScriptsTopdir = Path.Combine (appbase, "scripts");
			AndroidNdkR5 = Path.Combine (appbase, "ndk-r5");
			AndroidNdkCrystaxR4 = Path.Combine (appbase, "ndk-r4-crystax");
			AndroidNdkR4 = Path.Combine (appbase, "ndk-r4");
		}

		public static string GetAndroidRoot (NDKType type)
		{
			switch (type) {
			case NDKType.R5:
				return AndroidNdkR5;
			case NDKType.CrystaxR4:
				return AndroidNdkCrystaxR4;
			case NDKType.R4:
				return AndroidNdkR4;
			default:
				throw new ArgumentOutOfRangeException ("type");
			}
		}
	}
}
