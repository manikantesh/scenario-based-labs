using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using CosmosDbIoTScenario.Common;
using CosmosDbIoTScenario.Common.Models;
using Microsoft.Azure.Documents;
using Microsoft.Azure.Documents.Client;
using Microsoft.Azure.Documents.Linq;
using Microsoft.Azure.EventHubs;
using Microsoft.Azure.WebJobs;
using Microsoft.Azure.WebJobs.Host;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json;

namespace Functions.CosmosDB
{
    public static class Functions
    {
        [FunctionName("TripProcessor")]
        public static async Task TripProcessor([CosmosDBTrigger(
            databaseName: "ContosoAuto",
            collectionName: "telemetry",
            ConnectionStringSetting = "CosmosDBConnection",
            LeaseCollectionName = "leases",
            LeaseCollectionPrefix = "trips",
            CreateLeaseCollectionIfNotExists = true,
            StartFromBeginning = true)]IReadOnlyList<Document> vehicleEvents,
            [CosmosDB(
                databaseName: "ContosoAuto",
                collectionName: "metadata",
                ConnectionStringSetting = "CosmosDBConnection")] DocumentClient client,
            ILogger log)
        {
            log.LogInformation($"Evaluating {vehicleEvents.Count} events from Cosmos DB to optionally update Trip and Consignment metadata.");

            // Retrieve the Trip records by VIN, compare the odometer reading to the starting odometer reading to calculate miles driven,
            // and update the Trip status and send an alert if needed once completed.
            var sendTripCompletedAlert = false;
            var sendTripDelayedAlert = false;
            var database = "ContosoAuto";
            var metadataContainer = "metadata";

            if (vehicleEvents.Count > 0)
            {
                var collectionUri = UriFactory.CreateDocumentCollectionUri(database, metadataContainer);

                foreach (var group in vehicleEvents.GroupBy(singleEvent => singleEvent.GetPropertyValue<string>("vin")))
                {
                    var vin = group.Key;
                    var odometerHigh = group.Max(item => item.GetPropertyValue<double>("odometer"));

                    // Create a query, defining the partition key so we don't execute a fan-out query (saving RUs), where the entity type is a Trip and the status is not Completed, Canceled, or Inactive.
                    var query = client.CreateDocumentQuery<Trip>(collectionUri,
                            new FeedOptions { PartitionKey = new PartitionKey(vin) })
                        .Where(p => p.status != WellKnown.Status.Completed
                                    && p.status != WellKnown.Status.Canceled
                                    && p.status != WellKnown.Status.Inactive
                                    && p.entityType == WellKnown.EntityTypes.Trip)
                        .AsDocumentQuery();

                    if (query.HasMoreResults)
                    {
                        // Only retrieve the first result.
                        var result = await query.ExecuteNextAsync<Trip>();
                        var trip = result.FirstOrDefault();
                        
                        if (trip != null)
                        {
                            // Retrieve the Consignment record.
                            var document = await client.ReadDocumentAsync<Consignment>(UriFactory.CreateDocumentUri(database, metadataContainer, trip.consignmentId),
                                new RequestOptions { PartitionKey = new PartitionKey(trip.consignmentId) });
                            var consignment = document.Document;
                            var updateTrip = false;
                            var updateConsignment = false;

                            // Calculate how far along the vehicle is for this trip.
                            var milesDriven = odometerHigh - trip.odometerBegin;
                            if (milesDriven >= trip.plannedTripDistance)
                            {
                                // The trip is completed!
                                trip.status = WellKnown.Status.Completed;
                                trip.odometerEnd = odometerHigh;
                                trip.tripEnded = DateTime.UtcNow;
                                consignment.status = WellKnown.Status.Completed;

                                // Update the trip and consignment records.
                                updateTrip = true;
                                updateConsignment = true;

                                sendTripCompletedAlert = true;
                            }
                            else
                            {
                                if (DateTime.UtcNow >= consignment.deliveryDueDate && trip.status != WellKnown.Status.Delayed)
                                {
                                    // The trip is delayed!
                                    trip.status = WellKnown.Status.Delayed;
                                    consignment.status = WellKnown.Status.Delayed;

                                    // Update the trip and consignment records.
                                    updateTrip = true;
                                    updateConsignment = true;

                                    sendTripDelayedAlert = true;
                                }
                            }

                            if (trip.tripStarted == null)
                            {
                                // Set the trip start date.
                                trip.tripStarted = DateTime.UtcNow;
                                // Set the trip and consignment status to Active.
                                trip.status = WellKnown.Status.Active;
                                consignment.status = WellKnown.Status.Active;

                                updateTrip = true;
                                updateConsignment = true;
                            }

                            // Update the trip and consignment records.
                            if (updateTrip)
                            {
                                await client.ReplaceDocumentAsync(UriFactory.CreateDocumentUri(database, metadataContainer, trip.id), trip);
                            }

                            if (updateConsignment)
                            {
                                await client.ReplaceDocumentAsync(UriFactory.CreateDocumentUri(database, metadataContainer, consignment.id), consignment);
                            }
                        }
                    }
                }
            }
        }

        [FunctionName("ColdStorage")]
        public static async Task ChangeColdStorageFeedTrigger([CosmosDBTrigger(
            databaseName: "ContosoAuto",
            collectionName: "telemetry",
            ConnectionStringSetting = "CosmosDBConnection",
            LeaseCollectionName = "leases",
            LeaseCollectionPrefix = "cold",
            CreateLeaseCollectionIfNotExists = true,
            StartFromBeginning = true)]IReadOnlyList<Document> vehicleEvents,
            Binder binder,
            ILogger log)
        {
            log.LogInformation($"Saving {vehicleEvents.Count} events from Cosmos DB to cold storage.");

            if (vehicleEvents.Count > 0)
            {
                // Use imperative binding to Azure Storage, as opposed to declarative binding.
                // This allows us to compute the binding parameters and set the file path dynamically during runtime.
                var attributes = new Attribute[]
                {
                    new BlobAttribute($"telemetry/custom/scenario1/{DateTime.UtcNow:yyyy/MM/dd/HH/mm/ss-fffffff}.json", FileAccess.ReadWrite),
                    new StorageAccountAttribute("ColdStorageAccount")
                };

                using (var fileOutput = await binder.BindAsync<TextWriter>(attributes))
                {
                    // Write the data to Azure Storage for cold storage and batch processing requirements.
                    // Please note: Application Insights will log Dependency errors with a 404 result code for each write.
                    // The error is harmless since the internal Storage SDK returns a 404 when it first checks if the file already exists.
                    // Application Insights cannot distinguish between "good" and "bad" 404 responses for these calls. These errors can be ignored for now.
                    // For more information, see https://github.com/Azure/azure-functions-durable-extension/issues/593
                    fileOutput.Write(JsonConvert.SerializeObject(vehicleEvents));
                }
            }
        }

        [FunctionName("SendToEventHubsForReporting")]
        public static async Task SendToEventHubsForReporting([CosmosDBTrigger(
            databaseName: "ContosoAuto",
            collectionName: "telemetry",
            ConnectionStringSetting = "CosmosDBConnection",
            LeaseCollectionName = "leases",
            LeaseCollectionPrefix = "reporting",
            CreateLeaseCollectionIfNotExists = true,
            StartFromBeginning = true)]IReadOnlyList<Document> vehicleEvents,
            [EventHub("reporting", Connection = "EventHubsConnection")] IAsyncCollector<EventData> vehicleEventsOut,
            ILogger log)
        {
            log.LogInformation($"Sending {vehicleEvents.Count} Cosmos DB records to Event Hubs for reporting.");

            if (vehicleEvents.Count > 0)
            {
                foreach (var vehicleEvent in vehicleEvents)
                {
                    // Convert to a VehicleEvent class.
                    var vehicleEventOut = await vehicleEvent.ReadAsAsync<VehicleEvent>();
                    // Add to the Event Hub output collection.
                    await vehicleEventsOut.AddAsync(new EventData(
                        Encoding.UTF8.GetBytes(JsonConvert.SerializeObject(vehicleEventOut))));
                }
            }
        }
    }
}
