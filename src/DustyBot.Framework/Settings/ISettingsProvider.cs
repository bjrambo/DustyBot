﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace DustyBot.Framework.Settings
{
    public interface ISettingsProvider
    {
        Task<T> Read<T>(ulong serverId, bool createIfNeeded = true)
            where T : IServerSettings;

        Task<IEnumerable<T>> Read<T>()
            where T : IServerSettings;

        Task InterlockedModify<T>(ulong serverId, Action<T> action)
            where T : IServerSettings;

        Task<U> InterlockedModify<T, U>(ulong serverId, Func<T, U> action)
            where T : IServerSettings;
    }
}
