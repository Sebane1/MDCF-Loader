﻿using MareSynchronos.Utils;

namespace MareSynchronos.Mediator;

public abstract class MediatorSubscriberBase : IMediatorSubscriber
{
    public MareMediator Mediator { get; }
    protected MediatorSubscriberBase(MareMediator mediator)
    {
        Mediator = mediator;
    }

    public virtual void Dispose()
    {
        Logger.Verbose($"Disposing {GetType()}");
        Mediator.UnsubscribeAll(this);
    }
}