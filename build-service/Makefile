all: build-service.exe
	dmcs build-service.cs ../drosh/DroshModel.cs ../drosh/datastore.cs ../drosh/builder.cs -r:../drosh/ServiceStack.Redis.dll -r:../drosh/ServiceStack.Interfaces.dll -debug

run:
	MONO_PATH=../drosh mono --debug build-service.exe
