using Projects;

var builder = DistributedApplication.CreateBuilder(args);

builder.AddProject<Tunnel_Server>("tunnel-server");

builder.Build().Run();
