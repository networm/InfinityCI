using InfinityCI.Agent;

var builder = Host.CreateApplicationBuilder(args);
builder.Services.Configure<AgentOptions>(builder.Configuration.GetSection(AgentOptions.SectionName));
builder.Services.AddSingleton(sp => sp.GetRequiredService<Microsoft.Extensions.Options.IOptions<AgentOptions>>().Value);
builder.Services.AddSingleton<RemoteBuildRunner>();
builder.Services.AddHostedService<AgentWorker>();

var host = builder.Build();
host.Run();
