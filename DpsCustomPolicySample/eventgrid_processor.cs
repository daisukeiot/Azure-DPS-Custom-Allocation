// Default URL for triggering event grid function in the local environment.
// http://localhost:7071/runtime/webhooks/EventGrid?functionName={functionname}
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.Azure.WebJobs;
using Microsoft.Azure.EventGrid.Models;
using Microsoft.Azure.WebJobs.Extensions.EventGrid;
using Microsoft.Extensions.Logging;
using Microsoft.Azure.Devices;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Microsoft.Azure.DigitalTwins.Parser;

namespace DpsCustomPolicySample
{
    public static class eventgrid_processor
    {
        private const string IotHubDeviceConnected = "Microsoft.Devices.DeviceConnected";
        private const string IotHubDeviceDisconnected = "Microsoft.Devices.DeviceDisconnected";
        private const string IotHubDeviceCreated = "Microsoft.Devices.DeviceCreated";
        private const string IotHubDeviceDeleted = "Microsoft.Devices.DeviceDeleted";
        private static readonly string _iotHubConnectionString = Environment.GetEnvironmentVariable("IOTHUB_CS");
        private static readonly string _gitToken = Environment.GetEnvironmentVariable("GIT_TOKEN");
        private static readonly string _modelRepoUrl_Private = Environment.GetEnvironmentVariable("PRIVATE_MODEL_REPOSIROTY_URL");
        private static ServiceClient _serviceClient = null;
        private static RegistryManager _registryManager = null;
        private static ILogger _logger;
        private static DeviceModelResolver _resolver = null;

        [FunctionName("eventgrid_processor")]
        public static async Task Run([EventGridTrigger] EventGridEvent eventGridEvent, ILogger log)
        {
            // Process Device Events from IoT Hub
            // https://docs.microsoft.com/en-us/azure/event-grid/event-schema-iot-hub?tabs=event-grid-event-schema
            _logger = log;
            
            JObject deviceEventData = (JObject)JsonConvert.DeserializeObject(eventGridEvent.Data.ToString());
            //log.LogInformation($"Event Type {eventGridEvent.EventType.ToString()}");
            log.LogInformation(eventGridEvent.ToString());

            // Sanity check

            if (deviceEventData == null)
            {
                log.LogError("Invalid input : Event Data is NULL");
                return;
            }

            if (_serviceClient == null)
            {
                // Create IoT Hub Service Client
                // https://docs.microsoft.com/en-us/dotnet/api/microsoft.azure.devices.serviceclient?view=azure-dotnet
                _serviceClient = ServiceClient.CreateFromConnectionString(_iotHubConnectionString);
            }

            if (_serviceClient == null)
            {
                log.LogError("Failed to create to Service Client");
                return;
            }

            if (_registryManager == null)
            {
                // Create Registry Manager
                // https://docs.microsoft.com/en-us/dotnet/api/microsoft.azure.devices.registrymanager?view=azure-dotnet
                _registryManager = RegistryManager.CreateFromConnectionString(_iotHubConnectionString);
            }

            if (_registryManager == null)
            {
                log.LogError("Failed to create to Registry Manager");
                return;
            }

            switch (eventGridEvent.EventType)
            {
                case IotHubDeviceConnected:
                    await ProcessDeviceConnected(deviceEventData, log);
                    break;

                case IotHubDeviceDisconnected:
                    await ProcessDeviceDisconnected(deviceEventData, log);
                    break;

                case IotHubDeviceCreated:
                    await ProcessDeviceCreated(deviceEventData, log);
                    break;

                case IotHubDeviceDeleted:
                    await ProcessDeviceDeleted(deviceEventData, log);
                    break;

            }
        }
        private static async Task ProcessDeviceConnected(JObject deviceEventData, ILogger log)
        {
            log.LogInformation(">> DeviceConnected Event");
            // Process Device Connected Event.
            // https://docs.microsoft.com/en-us/azure/iot-hub/iot-hub-event-grid#device-connected-schema
            // Example of sending a direct method (command)
            try
            {
                // Get Device Id
                string deviceId = deviceEventData["deviceId"].ToString();

                // Get Device Instance from IoT Hub
                // https://docs.microsoft.com/en-us/dotnet/api/microsoft.azure.devices.registrymanager.getdeviceasync?view=azure-dotnet
                Device device = await _registryManager.GetDeviceAsync(deviceId);

                if (device.ConnectionState != DeviceConnectionState.Connected)
                {
                    log.LogWarning($"Device {deviceId} is not connected");
                    return;
                }

                // Get DTDL Model ID from Device Twin
                // https://docs.microsoft.com/en-us/dotnet/api/microsoft.azure.devices.registrymanager.gettwinasync?view=azure-dotnet
                var twin = await _registryManager.GetTwinAsync(deviceId);

                if (!string.IsNullOrEmpty(twin.ModelId))
                {
                    IReadOnlyDictionary<Dtmi, DTEntityInfo> parsedModel = null;
                    string commandName = string.Empty;

                    log.LogInformation($"Model ID : {twin.ModelId}");

                    // Parse DTDL Model
                    parsedModel = await DeviceModelResolveAndParse(twin.ModelId);

                    if (twin.ModelId.Contains("impinj"))
                    {
                        commandName = "Presets";
                    }
                    else if (twin.ModelId.Contains("wioterminal_aziot_example"))
                    {
                        commandName = "ringBuzzer";
                    }

                    // We are interested in only commands
                    DTCommandInfo command = parsedModel.Where(r => r.Value.EntityKind == DTEntityKind.Command).Select(x => x.Value as DTCommandInfo).Where(x => x.Name == commandName).FirstOrDefault();

                    if (command != null)
                    {
                        string componentName = string.Empty;
                        commandName = string.Empty;

                        // If no match, this interface must be from Component
                        if (!twin.ModelId.Equals(command.DefinedIn.AbsoluteUri))
                        {
                            var component = parsedModel.Where(r => r.Value.EntityKind == DTEntityKind.Component).Select(x => x.Value as DTComponentInfo).Where(x => x.Schema.Id.ToString() == command.ChildOf.AbsoluteUri).FirstOrDefault();
                            if (component != null)
                            {
                                componentName = component.Name;
                            }
                        }

                        if (!string.IsNullOrEmpty(componentName))
                        {
                        // Add component name
                        // https://docs.microsoft.com/en-us/azure/iot-pnp/concepts-convention#commands
                            commandName = $"{componentName}*{command.Name}";
                        }
                        else
                        {
                            commandName = $"{command.Name}";
                        }

                        log.LogInformation($"Sending command {commandName} / Description : {command.Description}");

                        var cmd = new CloudToDeviceMethod(commandName)
                        {
                            ResponseTimeout = TimeSpan.FromSeconds(30)
                        };

                        if (commandName.Equals("ringBuzzer"))
                        {
                            cmd.SetPayloadJson("500");
                        }
                        var response = await _serviceClient.InvokeDeviceMethodAsync(deviceId, cmd);
                        log.LogInformation($"Response status: {response.Status}, payload: {response.GetPayloadAsJson()}");
                    }
                }
            }
            catch (Exception ex)
            {
                log.LogWarning($"Failed to process Device Connected Event : Exception '{ex.Message}'");
            }
            log.LogInformation("<< DeviceConnected Event");
        }

        public static async Task ProcessDeviceDisconnected(JObject deviceEventData, ILogger log)
        {
            log.LogInformation(">> DeviceDisconnected Event");
            // Process Device Connected Event.
            // https://docs.microsoft.com/en-us/azure/iot-hub/iot-hub-event-grid#device-connected-schema
            // Example of sending a direct method (command)
            try
            {
                // for warning.
                await Task.Delay(100);
            }
            catch (Exception ex)
            {
                log.LogWarning($"Failed to process Device Connected Event : Exception '{ex.Message}'");
            }
            log.LogInformation("<< DeviceDisconnected Event");
        }

        public static async Task ProcessDeviceCreated(JObject deviceEventData, ILogger log)
        {
            log.LogInformation(">> DeviceCreated Event");
            // Process Device Created Event.
            // https://docs.microsoft.com/en-us/azure/iot-hub/iot-hub-event-grid#device-created-schema
            // Sets Device Twin desired property and Tag
            try
            {
                // Get Device Id
                string deviceId = deviceEventData["deviceId"].ToString();

                // Get Device Instance from IoT Hub
                // https://docs.microsoft.com/en-us/dotnet/api/microsoft.azure.devices.registrymanager.getdeviceasync?view=azure-dotnet
                Device device = await _registryManager.GetDeviceAsync(deviceId);

                // Get DTDL Model ID from Device Twin
                // https://docs.microsoft.com/en-us/dotnet/api/microsoft.azure.devices.registrymanager.gettwinasync?view=azure-dotnet
                var twin = await _registryManager.GetTwinAsync(deviceId);

                // We do not know Model ID at this point.  Model ID is passed by the device when it connects (Model ID Announcement).
                // Operations using Device model must be done in DPS Custom Allocation function.
                //
                twin.Tags["TagFromEventGrid"] = "Processed";
                twin = await _registryManager.UpdateTwinAsync(deviceId, twin, twin.ETag);
            }
            catch (Exception ex)
            {
                log.LogWarning($"Failed to process Device Connected Event : Exception '{ex.Message}'");
            }
            log.LogInformation("<< DeviceCreated Event");
        }

        public static async Task ProcessDeviceDeleted(JObject deviceEventData, ILogger log)
        {
            // Process Device Deleted Event.
            try
            {
                // for warning.
                await Task.Delay(100);
            }
            catch (Exception ex)
            {
                log.LogWarning($"Failed to process Device Connected Event : Exception '{ex.Message}'");
            }
        }
        private static async Task<IReadOnlyDictionary<Dtmi, DTEntityInfo>> DeviceModelResolveAndParse(string dtmi)
        {
            if (!string.IsNullOrEmpty(dtmi))
            {
                try
                {
                    if (_resolver == null)
                    {
                        _resolver = new DeviceModelResolver(_modelRepoUrl_Private, _gitToken, _logger);
                    }

                    // resolve and parse device model
                    return await _resolver.ParseModelAsync(dtmi);

                }
                catch (Exception e)
                {
                    _logger.LogError($"Error Resolve(): {e.Message}");
                }
            }

            return null;
        }
    }
}
