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

namespace DpsCustomPolicySample
{
    public static class dps_processor
    {
        [FunctionName("dps_processor")]
        public static async Task<IActionResult> Run(
            [HttpTrigger(AuthorizationLevel.Function, "get", "post", Route = null)] HttpRequest request,
            ILogger log)
        {
            string requestBody = await new StreamReader(request.Body).ReadToEndAsync();
            string errorMessage = string.Empty;
            DpsResponse response = new DpsResponse();

            dynamic requestData = JsonConvert.DeserializeObject(requestBody);

            string registrationId = requestData?.deviceRuntimeContext?.registrationId;
            string enrollmentGroupId = string.Empty;
            string[] iothubs = requestData?.linkedHubs.ToObject<string[]>();

            log.LogInformation($"dps_processor : Request.Body: \r\n{JsonConvert.SerializeObject(requestData, Formatting.Indented)}");

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

            try
            {

                if (registrationId != null)
                {
                    log.LogInformation($"Registration ID: {registrationId}");
                }
                else if (requestData["enrollmentGroup"]["enrollmentGroupId"] != null)
                {
                    enrollmentGroupId = requestData?.enrollmentGroup?.enrollmentGroupId;
                    log.LogInformation($"Enrollment Group: {enrollmentGroupId}");
                }

                if (string.IsNullOrEmpty(registrationId) && string.IsNullOrEmpty(enrollmentGroupId))
                {
                    errorMessage = "Registration ID empty";
                    log.LogInformation("Error : Registrion ID empty");
                }
                else if (iothubs == null)
                {
                    errorMessage = "No linked hubs for this enrollment.";
                    log.LogInformation("Error : No linked IoT Hub");
                }
                else
                {
                    if (!string.IsNullOrEmpty(enrollmentGroupId) && enrollmentGroupId.Contains("PVDemo-Group-Enrollment-Custom-Allocation"))
                    {
                        // do specifics based on registration id
                    }

                    if (!string.IsNullOrEmpty(enrollmentGroupId) && enrollmentGroupId.Contains("PVDemo-Group-Enrollment-Custom-Allocation"))
                    {
                        // do specifics based on registration id
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
                    desiredProperties["DesiredTest2"] = string.IsNullOrEmpty(registrationId)?enrollmentGroupId:registrationId;
                    desiredProperties["DpsRegistrationId"] = registrationId;

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

        public class DpsResponse
        {
            public string iotHubHostName { get; set; }
            public TwinState initialTwin { get; set; }
        }
    }
}
