all: build-service.exe

build-service.exe:
	dmcs build-service.cs ../drosh/DroshModel.cs ../drosh/consts.cs ../drosh/datastore.cs ../drosh/builder.cs -r:../drosh/ServiceStack.Redis.dll -r:../drosh/ServiceStack.Interfaces.dll -r:Mono.Posix.dll -debug

