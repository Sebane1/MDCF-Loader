﻿using Dalamud.Plugin.Services;
using Microsoft.Extensions.Logging;

namespace MareSynchronos.Services.Mediator;

public abstract class MediatorSubscriberBase : IMediatorSubscriber
{
    protected MediatorSubscriberBase(McdfMediator mediator)
    {
        Logger = EntryPoint.PluginLog;

        Logger.Warning("Creating {type} ({this})", GetType().Name, this);
        Mediator = mediator;
    }

    public McdfMediator Mediator { get; }
    protected IPluginLog Logger { get; }

    protected void UnsubscribeAll()
    {
        Logger.Warning("Unsubscribing from all for {type} ({this})", GetType().Name, this);
        Mediator.UnsubscribeAll(this);
    }
}