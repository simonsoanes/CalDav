// Copyright 2026 Simon Soanes
// SPDX-License-Identifier: Apache-2.0

using System.Threading.Channels;

namespace Nullify.CalDav.Data;

/// <summary>A change to a collection's contents (resources) or its own properties.</summary>
public sealed record CollectionChange(string Type, int CollectionId, bool Content)
{
    public const string Calendar = "calendar";
    public const string AddressBook = "addressbook";
}

/// <summary>Fan-out point for collection changes, consumed by background services (push, birthdays).</summary>
public sealed class CollectionChangeNotifier
{
    private readonly Channel<CollectionChange> _channel =
        Channel.CreateBounded<CollectionChange>(new BoundedChannelOptions(10_000) { FullMode = BoundedChannelFullMode.DropOldest });

    public ChannelReader<CollectionChange> Reader => _channel.Reader;

    /// <summary>Raised when accounts are created, changed or deleted (used to rebuild the directory).</summary>
    public event Action? UsersChanged;

    public void RaiseUsersChanged() => UsersChanged?.Invoke();

    public void Publish(IEnumerable<CollectionChange> changes)
    {
        foreach (var c in changes.Distinct()) _channel.Writer.TryWrite(c);
    }
}
