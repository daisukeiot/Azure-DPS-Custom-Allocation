using System;
using System.IO;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Azure.WebJobs;
using Microsoft.Azure.WebJobs.Extensions.Http;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json;
using Microsoft.Azure.Devices.Shared;               // For TwinCollection
using Microsoft.Azure.Devices.Provisioning.Service; // For TwinState
using System.Collections.Generic;
using Microsoft.Azure.DigitalTwins.Parser;
using System.Linq;

namespace DpsCustomPolicySample
{
    public static class dps_processor
    {
        private static readonly string _gitToken = Environment.GetEnvironmentVariable("GIT_TOKEN");
        private static readonly string _modelRepoUrl_Private = Environment.GetEnvironmentVariable("PRIVATE_MODEL_REPOSIROTY_URL");
        private static ILogger _logger;
        private static DeviceModelResolver _resolver = null;

        [FunctionName("dps_processor")]
        public static async Task<IActionResult> Run(
            [HttpTrigger(AuthorizationLevel.Function, "get", "post", Route = null)] HttpRequest request,
            ILogger log)
        {
            string requestBody = await new StreamReader(request.Body).ReadToEndAsync();
            string errorMessage = string.Empty;
            DpsResponse response = new DpsResponse();
            bool isGroupEnrollment = false;
            string registrationId;

            _logger = log;

            dynamic requestData = JsonConvert.DeserializeObject(requestBody);

            if (requestData.ContainsKey("enrollmentGroup"))
            {
                log.LogInformation("Group Enrollment");
                registrationId = requestData?.enrollmentGroup?.enrollmentGroupId;
                isGroupEnrollment = true;
            }
            else
            {
                log.LogInformation("Individual Enrollment");
                registrationId = requestData?.deviceRuntimeContext?.registrationId;
            }

            string[] iothubs = requestData?.linkedHubs.ToObject<string[]>();

            log.LogInformation($"dps_processor : Request.Body: {JsonConvert.SerializeObject(requestData, Formatting.Indented)}");

            #region payload_sample
            /* Payload Example
            {
              "enrollmentGroup": {
                "enrollmentGroupId": "PVDemo-Group-Enrollment-Custom-Allocation",
                "attestation": {
                  "type": "symmetricKey"
                },
                "capabilities": {
                  "iotEdge": false
                },
                "etag": "\"1a055216-0000-0800-0000-60a637160000\"",
                "provisioningStatus": "enabled",
                "reprovisionPolicy": {
                  "updateHubAssignment": true,
                  "migrateDeviceData": true
                },
                "createdDateTimeUtc": "2021-05-20T10:15:51.2294536Z",
                "lastUpdatedDateTimeUtc": "2021-05-20T10:16:54.6543548Z",
                "allocationPolicy": "custom",
                "iotHubs": [
                  "PVDemo-IoTHub.azure-devices.net"
                ],
                "customAllocationDefinition": {
                  "webhookUrl": "https://pvdemo-functions.azurewebsites.net/api/dps_processor?****",
                  "apiVersion": "2019-03-31"
                }
              },
              "deviceRuntimeContext": {
                "registrationId": "WioTerminal",
                "symmetricKey": {},
                "payload": {
                  "modelId": "dtmi:seeedkk:wioterminal:wioterminal_aziot_example_gps;5"
                }
              },
              "linkedHubs": [
                "PVDemo-IoTHub.azure-devices.net"
              ]
            }
            */
            #endregion

            try
            {
                if (registrationId == null)
                {
                    log.LogError($"Missing Registration ID");
                }
                else if (iothubs == null)
                {
                    errorMessage = "No linked hubs for this enrollment.";
                    log.LogError("linked IoT Hub");
                }
                else
                {
                    // Example of specific tasks based on Enrollment
                    if (registrationId.Contains("Group"))
                    {
                        // do specifics based on registration id
                    }

                    if (isGroupEnrollment)
                    {
                        // do specifics for Group Enrollment
                    }
                    else
                    {
                        // do specifics for Individual Enrollment
                    }

                    foreach (var iothub in iothubs)
                    {
                        // do specifics for linked hubs
                        // e.g. pick up right IoT Hub based on device id
                        response.iotHubHostName = iothub;
                    }


                    // build tags for the device
                    // tags are for solution only, devices do not see tags
                    // https://docs.microsoft.com/en-us/azure/iot-hub/iot-hub-devguide-device-twins#device-twins
                    TwinCollection twinTags = new TwinCollection();
                    twinTags["TagExample"] = "CustomAllocationSample";

                    // build initial twin (Desired Properties) for the device
                    // these values will be passed to the device during Initial Get
                    TwinCollection desiredProperties = new TwinCollection();
                    desiredProperties["DesiredTest1"] = "InitilTwinByCustomAllocation";
                    desiredProperties["DesiredTest2"] = registrationId;
                    desiredProperties["DpsRegistrationId"] = registrationId;

                    // Get DTDL Model Id
                    string componentName = string.Empty;
                    IReadOnlyDictionary<Dtmi, DTEntityInfo> parsedModel = null;
                    string modelId = requestData?.deviceRuntimeContext?.payload?.modelId;

                    if (!string.IsNullOrEmpty(modelId))
                    {
                        // If DTMI is given to DPS payload, parse it.
                        parsedModel = await DeviceModelResolveAndParse(modelId);
                    }

                    if (parsedModel != null)
                    {
                        string propertyName = "Hostname";
                        // Example : Setting Writable Property using Device Model
                        // We are interested in properties
                        DTPropertyInfo property = parsedModel.Where(r => r.Value.EntityKind == DTEntityKind.Property).Select(x => x.Value as DTPropertyInfo).Where(x => x.Writable == true).Where(x => x.Name == propertyName).FirstOrDefault();
                        
                        if (property != null)
                        {
                            log.LogInformation($"Found Writable Property '{propertyName}'");

                            // If no match, this interface must be from Component
                            if (!modelId.Equals(property.DefinedIn.AbsoluteUri))
                            {
                                var component = parsedModel.Where(r => r.Value.EntityKind == DTEntityKind.Component).Select(x => x.Value as DTComponentInfo).Where(x => x.Schema.Id.ToString() == property.ChildOf.AbsoluteUri).FirstOrDefault();
                                if (component != null)
                                {
                                    TwinCollection componentTwin = new TwinCollection();
                                    TwinCollection hostnameComponentTwin = new TwinCollection();
                                    // Hostname takes a parameter as JSON Object
                                    // JSON looks like this
                                    // "desired" : {
                                    //   "R700": {
                                    //     "__t": "c",
                                    //     "Hostname" : {
                                    //       "hostname" : "<New Name>"
                                    //     }
                                    //   }
                                    // }
                                    if (property.Schema.EntityKind == DTEntityKind.Object)
                                    {
                                        DTObjectInfo parameterObj = property.Schema as DTObjectInfo;
                                        hostnameComponentTwin[parameterObj.Fields[0].Name] = "impinj-14-04-63-01-Functions";
                                        componentTwin[property.Name] = hostnameComponentTwin;
                                        componentTwin["__t"] = "c";
                                        desiredProperties[component.Name] = componentTwin;
                                    }
                                }
                            }
                            else
                            {
                                desiredProperties[property.Name] = "impinj-14-04-63-01-functions";
                            }

                        }
                    }

                    TwinState twinState = new TwinState(twinTags, desiredProperties);

                    response.initialTwin = twinState;
                }
            }
            catch (Exception ex)
            {
                log.LogError($"Exception {ex}");
                errorMessage = ex.Message;
            }

            if (!string.IsNullOrEmpty(errorMessage))
            {
                log.LogError($"Error : {errorMessage}");
                return new BadRequestObjectResult(errorMessage);
            }

            log.LogInformation($"Response to DPS \r\n {JsonConvert.SerializeObject(response)}");
            return (ActionResult)new OkObjectResult(response);
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

        public class DpsResponse
        {
            public string iotHubHostName { get; set; }
            public TwinState initialTwin { get; set; }
        }
    }
}
