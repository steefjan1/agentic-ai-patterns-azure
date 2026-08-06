using Azure.Messaging.EventGrid;

namespace MeshFunctions.Services;

/// <summary>Every agent in the mesh publishes through this — none of them know who's listening.</summary>
public class EventPublisherService
{
    private readonly EventGridPublisherClient _client;

    public EventPublisherService(EventGridPublisherClient client) => _client = client;

    public async Task PublishAsync(string eventType, string subject, object data)
    {
        var evt = new EventGridEvent(subject, eventType, "1.0", data);
        await _client.SendEventAsync(evt);
    }
}
