using Sunder.Package.Agent.Provider.OpenAI.Worker;
using Sunder.Sdk.Worker;

await SunderWorker.RunAsync(OpenAiWorkerConfiguration.Create);
