using System.Collections.Concurrent;

namespace Relay.Core.Rooms;

public interface IRoomManager
{
    IReadOnlyCollection<ChatRoom> Rooms { get; }

    ChatRoom CreateRoom(string name);

    ChatRoom? GetRoom(Guid id);
}

public sealed class InMemoryRoomManager : IRoomManager
{
    private readonly ConcurrentDictionary<Guid, ChatRoom> _rooms = new();

    public IReadOnlyCollection<ChatRoom> Rooms => [.. _rooms.Values];

    public ChatRoom CreateRoom(string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        var room = new ChatRoom(Guid.NewGuid(), name.Trim());
        _rooms[room.Id] = room;
        return room;
    }

    public ChatRoom? GetRoom(Guid id) => _rooms.GetValueOrDefault(id);
}
