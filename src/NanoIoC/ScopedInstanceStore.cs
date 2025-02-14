﻿using System;
using System.Collections.Generic;
using System.Threading;
using Microsoft.Extensions.DependencyInjection;

namespace NanoIoC
{
	/// <summary>
	/// Stores instances in the current ExecutionContext
	/// </summary>
	sealed class ScopedInstanceStore : InstanceStore
	{
		readonly IContainer container;
		readonly AsyncLocal<IDictionary<Type, IList<Tuple<Registration, object>>>> registrationStore;
		readonly AsyncLocal<IDictionary<Type, IList<Registration>>> injectedRegistrations;
		readonly AsyncLocal<object> mutex;
		readonly Guid id = new Guid();
		protected override ServiceLifetime ServiceLifetime => ServiceLifetime.Scoped;

		public override object Mutex => this.container.HttpContextItemsGetter() == null
			? this.mutex.Value ?? (this.mutex.Value = new object())
			: this.GetCurrentContextInstanceStore();

		public ScopedInstanceStore(IContainer container)
		{
			this.container = container;
			this.registrationStore = new AsyncLocal<IDictionary<Type, IList<Tuple<Registration, object>>>>
			{
				Value = new Dictionary<Type, IList<Tuple<Registration, object>>>()
			};
			this.injectedRegistrations = new AsyncLocal<IDictionary<Type, IList<Registration>>>
			{
				Value = new Dictionary<Type, IList<Registration>>()
			};
			this.mutex = new AsyncLocal<object>
			{
				Value = new object()
			};
		}

		protected override IDictionary<Type, IList<Tuple<Registration, object>>> Store
		{
			get
			{
				if (this.container.HttpContextItemsGetter() != null)
					return this.GetCurrentContextInstanceStore() as IDictionary<Type, IList<Tuple<Registration, object>>>;

				if (this.registrationStore.Value == null)
					this.registrationStore.Value = new Dictionary<Type, IList<Tuple<Registration, object>>>();

				return this.registrationStore.Value;
			}
		}

		private object GetCurrentContextInstanceStore()
		{
			return this.container.HttpContextItemsGetter()["__NanoIoC_InstanceStore_" + this.id] ??
				   (this.container.HttpContextItemsGetter()["__NanoIoC_InstanceStore_" + this.id] = new Dictionary<Type, IList<Tuple<Registration, object>>>());
		}

		protected override IDictionary<Type, IList<Registration>> InjectedRegistrations
		{
			get
			{
				if (this.container.HttpContextItemsGetter() != null)
				{
					if (this.container.HttpContextItemsGetter()["__NanoIoC_InjectedRegistrations_" + this.id] == null)
						this.container.HttpContextItemsGetter()["__NanoIoC_InjectedRegistrations_" + this.id] = new Dictionary<Type, IList<Registration>>();

					return this.container.HttpContextItemsGetter()["__NanoIoC_InjectedRegistrations_" + this.id] as IDictionary<Type, IList<Registration>>;
				}

				if (this.injectedRegistrations.Value == null)
					this.injectedRegistrations.Value = new Dictionary<Type, IList<Registration>>();

				return this.injectedRegistrations.Value;
			}
		}

		public override IInstanceStore Clone()
		{
			var instanceStore = new ScopedInstanceStore(this.container);

			if (this.container.HttpContextItemsGetter() != null)
			{
				// todo: replace ILists with new lists, and registrations with new registrations
				this.container.HttpContextItemsGetter()["__NanoIoC_InstanceStore_" + instanceStore.id] = new Dictionary<Type, IList<Tuple<Registration, object>>>(this.Store);
				this.container.HttpContextItemsGetter()["__NanoIoC_InjectedRegistrations_" + instanceStore.id] = new Dictionary<Type, IList<Registration>>(this.InjectedRegistrations);
			}
			else
			{
				// todo: replace ILists with new lists, and registrations with new registrations
				instanceStore.registrationStore.Value = new Dictionary<Type, IList<Tuple<Registration, object>>>(this.Store);
				instanceStore.injectedRegistrations.Value = new Dictionary<Type, IList<Registration>>(this.InjectedRegistrations);
			}

			instanceStore.Registrations = new Dictionary<Type, IList<Registration>>(this.Registrations);

			return instanceStore;
		}
	}
}