using System;
using Manos;
using Manos.Spark;

namespace drosh {

	public class drosh : ManosApp {

		public drosh ()
		{
			Route ("/Content/", new StaticContentModule ());
			Route ("/", ctx => ctx.Response.End ("manos test"));
			Route ("/Spark", Spark);
		}

		public void Spark (IManosContext ctx)
		{
			this.RenderSparkView (ctx, "index.spark", new {
				Title = "spark test"
			});
			ctx.Response.End ();
		}
	}
}
