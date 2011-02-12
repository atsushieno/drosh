

namespace drosh
{
	public class BuildService
	{
		public static void Main (string [] args)
		{
			if (args.Length > 0) {
				foreach (var arg in args)
					Builder.ProcessBuild (arg);
			}
			else
				Builder.ProcessBuilds ();
		}
	}
}

