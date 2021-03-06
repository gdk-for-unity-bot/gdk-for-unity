using System;
using Improbable.Gdk.Core;
using Unity.Entities;

namespace Improbable.Gdk.Subscriptions
{
    internal class EntityAddedCallbackManager : ICallbackManager
    {
        private readonly Callbacks<EntityId> callbacks = new Callbacks<EntityId>();
        private readonly EntitySystem entitySystem;

        private ulong nextCallbackId = 1;

        public EntityAddedCallbackManager(World world)
        {
            entitySystem = world.GetExistingSystem<EntitySystem>();
        }

        public void InvokeCallbacks()
        {
            var entities = entitySystem.GetEntitiesAdded();
            for (int i = 0; i < entities.Count; ++i)
            {
                callbacks.InvokeAll(entities[i]);
            }
        }

        public ulong RegisterCallback(Action<EntityId> callback)
        {
            callbacks.Add(nextCallbackId, callback);
            return nextCallbackId++;
        }

        public bool UnregisterCallback(ulong callbackKey)
        {
            return callbacks.Remove(callbackKey);
        }
    }
}
