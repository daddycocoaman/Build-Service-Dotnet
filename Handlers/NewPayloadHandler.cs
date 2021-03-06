using System;
using System.IO;
using System.Collections.Generic;
using System.Diagnostics;
using System.Text;
using System.Threading.Tasks;
using System.Net;
using Newtonsoft.Json;

using Faction.Common;
using Faction.Common.Backend.Database;
using Faction.Common.Backend.EventBus.Abstractions;
using Faction.Common.Messages;
using Faction.Common.Models;

using Faction.Build.Dotnet;

namespace Faction.Build.Dotnet.Handlers
{
  public class NewPayloadEventHandler : IEventHandler<NewPayload>
  {
    public string apiUrl = "http://api:5000/api/v1/payload";
    private readonly IEventBus _eventBus;
    private static FactionRepository _taskRepository;

    public NewPayloadEventHandler(IEventBus eventBus, FactionRepository taskRepository)
    {
      _eventBus = eventBus; // Inject the EventBus into this Handler to Publish a message, insert AppDbContext here for DB Access
      _taskRepository = taskRepository;
    }

    public Dictionary<string, string> RunCommand(string agentDirectory, string cmd) {
      var escapedArgs = cmd.Replace("\"", "\\\"");
      Console.WriteLine($"[i] Executing build command: {escapedArgs}");
      Process proc = new Process()
      {
        StartInfo = new ProcessStartInfo
        {
          FileName = "/bin/bash",
          Arguments = $"-c \"{escapedArgs}\"",
          RedirectStandardError = true,
          RedirectStandardOutput = true,
          UseShellExecute = false,
          CreateNoWindow = true,
          WorkingDirectory = agentDirectory
        }
      };

      proc.Start();
      proc.WaitForExit();
      
      string output = proc.StandardOutput.ReadToEnd();
      string error = proc.StandardError.ReadToEnd();

      Dictionary<string, string> result = new Dictionary<string, string>();
      result["ExitCode"] = proc.ExitCode.ToString();
      result["Output"] = output;
      result["Error"] = error;
      return result;

    }

    public async Task Handle(NewPayload newPayload, string replyTo, string correlationId)
    {
      Console.WriteLine($"[i] Got New Payload Message.");
      Payload payload = new Payload();
      // Decode and Decrypt AgentTaskResponse
      payload.AgentType = _taskRepository.GetAgentType(newPayload.AgentTypeId);
      payload.AgentTypeFormat = _taskRepository.GetAgentTypeFormat(newPayload.AgentTypeFormatId);
      payload.AgentTransportType = _taskRepository.GetAgentTransportType(newPayload.AgentTransportTypeId);
      payload.Transport = _taskRepository.GetTransport(newPayload.TransportId);
      payload.Name = newPayload.Name;
      payload.Description = newPayload.Description;
      payload.Jitter = newPayload.Jitter;
      payload.BeaconInterval = newPayload.BeaconInterval;
      payload.ExpirationDate = newPayload.ExpirationDate;
      payload.BuildToken = newPayload.BuildToken;

      payload.Created = DateTime.UtcNow;
      payload.Enabled = true;
      payload.Visible = true;
      payload.Built = false;
      payload.Key = Utility.GenerateSecureString(32);
      payload.LanguageId = Settings.LanguageId;
      _taskRepository.Add(payload);
      _eventBus.Publish(payload, replyTo, correlationId);

      string workingDir = Path.Join(Settings.AgentsPath, payload.AgentType.Name);

      // Build transport first
      File.Delete(Path.Join(workingDir, payload.AgentTransportType.BuildLocation));

      string transportBuildCommand = payload.AgentTransportType.BuildCommand;

      List<Dictionary<string, string>> transportBuildConfig = JsonConvert.DeserializeObject<List<Dictionary<string, string>>>(payload.Transport.Configuration);
      foreach (Dictionary<string, string> configEntry in transportBuildConfig) {
        transportBuildCommand = transportBuildCommand.Replace(configEntry["Name"], Convert.ToBase64String(Encoding.UTF8.GetBytes(configEntry["Value"])));
      }

      Dictionary<string, string> cmdResult = RunCommand(workingDir, transportBuildCommand);
      string transportB64 = "";
      if (cmdResult["ExitCode"] == "0") {
        byte[] transportBytes = File.ReadAllBytes(Path.Join(workingDir, payload.AgentTransportType.BuildLocation));
        transportB64 = Convert.ToBase64String(transportBytes);
      }
      else {
        Console.WriteLine($"ERROR DURING TRANSPORT BUILD: \nStdout: {cmdResult["Output"]}\n Stderr: {cmdResult["Error"]}");
        NewErrorMessage response = new NewErrorMessage();
        response.Source = ".NET Build Server";
        response.Message = $"Error building {payload.AgentType.Name}";
        response.Details = $"Stdout: {cmdResult["Output"]}\n Stderr: {cmdResult["Error"]}";
        _eventBus.Publish(response, replyTo=null, correlationId=null);
      }

      // Build the agent
      if (!String.IsNullOrEmpty(transportB64)) {
        File.Delete(Path.Join(workingDir, payload.AgentTypeFormat.BuildLocation));
        string buildCommand = payload.AgentTypeFormat.BuildCommand.Replace("PAYLOADNAME", payload.Name);
        buildCommand = buildCommand.Replace("PAYLOADKEY", payload.Key);
        buildCommand = buildCommand.Replace("TRANSPORT", transportB64);
        buildCommand = buildCommand.Replace("BEACONINTERVAL", payload.BeaconInterval.ToString());
        buildCommand = buildCommand.Replace("JITTER", payload.Jitter.ToString());
        if (payload.ExpirationDate.HasValue)
        {
          buildCommand = buildCommand.Replace("EXPIRATION", payload.ExpirationDate.Value.ToString("o"));
        }
        else
        {
          buildCommand = buildCommand.Replace("EXPIRATION", "");
        }
        cmdResult = RunCommand(workingDir, buildCommand);

        if (cmdResult["ExitCode"] == "0") {
          try {
            Console.WriteLine($"[PayloadBuildService] Build Successful!");
            string originalPath = Path.Join(workingDir, payload.AgentTypeFormat.BuildLocation);
            string fileExtension = Path.GetExtension(originalPath);
            string payloadPath = Path.Join(Settings.AgentsPath, "/build/", $"{payload.AgentType.Name}_{payload.AgentTypeFormat.Name}_{payload.Name}_{DateTime.Now.ToString("yyyyMMddHHmmss")}{fileExtension}");
            Console.WriteLine($"[PayloadBuildService] Moving from {originalPath} to {payloadPath}");
            File.Move(originalPath, payloadPath);
            string uploadUlr = $"{apiUrl}/{payload.Id}/file/";
            WebClient wc = new WebClient();
            wc.Headers.Add("build-token", payload.BuildToken);
            Console.WriteLine($"[PayloadBuildService] Uploading to {uploadUlr} with token {payload.BuildToken}");
            byte[] resp = wc.UploadFile(uploadUlr, payloadPath);
            Console.WriteLine($"[PayloadBuildService] Response: {wc.Encoding.GetString(resp)}");
          }
          catch (Exception e) {
            Console.WriteLine($"ERROR UPLOADING PAYLOAD TO API: \n{e.Message}");
            NewErrorMessage response = new NewErrorMessage();
            response.Source = ".NET Build Server";
            response.Message = $"Error uploading {payload.AgentType.Name} payload to API";
            response.Details = $"{e.Message}";
            _eventBus.Publish(response, replyTo=null, correlationId=null);
          }
        }
        else {
          Console.WriteLine($"ERROR DURING AGENT BUILD: \nStdout: {cmdResult["Output"]}\n Stderr: {cmdResult["Error"]}");
          NewErrorMessage response = new NewErrorMessage();
          response.Source = ".NET Build Server";
          response.Message = $"Error building {payload.AgentType.Name}";
          response.Details = $"Stdout: {cmdResult["Output"]}\n Stderr: {cmdResult["Error"]}";
          _eventBus.Publish(response, replyTo=null, correlationId=null);
        }
      }
    }
  }
}