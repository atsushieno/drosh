all:
	dmcs -debug -t:library -out:drosh.dll $(SOURCES) $(REFS)

SOURCES = \
	drosh.cs \
	consts.cs \
	datastore.cs \
	builder.cs \
	DroshModel.cs \
	Manos.Spark.cs \
	StaticContentModule.cs

REFS = \
	`pkg-config --libs manos` \
	-r:Mono.Posix.dll \
	-r:ServiceStack.Client.dll \
	-r:ServiceStack.Common.dll \
	-r:ServiceStack.Interfaces.dll \
	-r:ServiceStack.Messaging.dll \
	-r:ServiceStack.Redis.dll \
	-r:ServiceStack.Text.dll \
	-r:Spark.dll


clean:
	rm -rf manos.dll manos.dll.mdb
