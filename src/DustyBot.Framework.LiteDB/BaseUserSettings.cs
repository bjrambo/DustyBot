﻿using System;
using System.Collections.Generic;
using System.Text;
using DustyBot.Framework.Settings;
using LiteDB;

namespace DustyBot.Framework.LiteDB
{
    public abstract class BaseUserSettings : IUserSettings
    {
        [BsonId]
        public ulong UserId { get; set; }
    }
}
