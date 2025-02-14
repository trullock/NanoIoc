﻿using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Microsoft.Extensions.DependencyInjection;

namespace NanoIoC
{
	public sealed partial class Container : MarshalByRefObject, IContainer
	{
		readonly IInstanceStore singletonInstanceStore;
		readonly IInstanceStore scopedStore;
		readonly IInstanceStore transientInstanceStore;
		readonly object mutex;
		
		/// <summary>
		/// Global container instance
		/// </summary>
		[Obsolete("Do not use, this is an antipattern")]
		public static readonly IContainer Global;

		static Container()
		{
			Global = new Container();
		}
		
		public Container()
		{
			this.mutex = new object();
			this.HttpContextItemsGetter = () => null;

			this.singletonInstanceStore = new SingletonInstanceStore();
			this.scopedStore = new ScopedInstanceStore(this);
			this.transientInstanceStore = new TransientInstanceStore();

			this.Inject<IContainer>(this);
			this.Inject<IResolverContainer>(this);
		}

		internal Container(Container container)
		{
			this.mutex = new object();
			this.singletonInstanceStore = container.singletonInstanceStore.Clone();
			this.scopedStore = container.scopedStore.Clone();
			this.transientInstanceStore = container.transientInstanceStore.Clone();

			// remove old container
			this.RemoveAllRegistrationsAndInstancesOf<IContainer>();

			// the contain can resolve itself);
			this.Inject<IContainer>(this);
		}

		public object Resolve(Type type)
		{
			return this.Resolve(type, null, new Stack<Type>());
		}

		public object Resolve(Type type, params object[] dependencies)
		{
			return this.Resolve(type, new TempInstanceStore(dependencies), new Stack<Type>());
		}

		/// <inheritdoc />
		public GraphNode DependencyGraph(Type type)
		{
			return this.DependencyGraph_Visit(type, new Stack<Type>());
		}

		/// <inheritdoc />
		public GraphNode DependencyGraph<T>()
		{
			return this.DependencyGraph(typeof(T));
		}

		GraphNode DependencyGraph_Visit(Type type, Stack<Type> buildStack)
		{
			var registrations = this.GetRegistrationsForTypesToCreate(type, buildStack);

			if (registrations.Count() > 1)
			{
				var enumerableNode = new GraphNode(new Registration(typeof(IEnumerable<>).MakeGenericType(type), null, null, ServiceLifetime.Transient, InjectionBehaviour.Default));

				foreach (var reg in registrations)
				{
					var regNode = new GraphNode(reg);
					enumerableNode.Dependencies.Add(regNode);
					this.DependencyGraph_VisitCtor(reg, regNode, new Stack<Type>(buildStack.Reverse()));
				}

				return enumerableNode;
			}

			var registration = registrations.First();

			var node = new GraphNode(registration);
			this.DependencyGraph_VisitCtor(registration, node, buildStack);
			
			return node;
		}

		void DependencyGraph_VisitCtor(Registration registration, GraphNode node, Stack<Type> buildStack)
		{
			if (registration.Ctor != null)
			{
				// cant handle this
			}
			else
			{
				var constructors = GetConstructors(registration);
				foreach (var ctor in constructors)
				{
					var parameterInfos = ctor.parameters.Select(p => p.ParameterType);

					this.CheckDependencies(registration.ConcreteType, parameterInfos, registration.ServiceLifetime, null, buildStack);

					for (var i = 0; i < ctor.parameters.Length; i++)
					{
						var newBuildStack = new Stack<Type>(buildStack.Reverse());
						if (ctor.parameters[i].ParameterType.IsGenericType && ctor.parameters[i].ParameterType.GetGenericTypeDefinition() == typeof(IEnumerable<>))
						{
							var genericArgument = ctor.parameters[i].ParameterType.GetGenericArguments()[0];
							node.Dependencies.Add(this.DependencyGraph_Visit(genericArgument, newBuildStack));
						}
						else
						{
							node.Dependencies.Add(this.DependencyGraph_Visit(ctor.parameters[i].ParameterType, newBuildStack));
						}
					}

					break;
				}

				//throw new ContainerException("Unable to construct `" + registration.ConcreteType.GetNameForException() + "`", buildStack);
			}
		}

		static IEnumerable<TypeCtor> GetConstructors(Registration registration)
		{
			try
			{
				var constructors = registration.ConcreteType.GetConstructors();
				var ctorsWithParams = constructors.Select(c => new TypeCtor(c, c.GetParameters()));
				var orderedEnumerable = ctorsWithParams.OrderBy(x => x.parameters.Length).ToArray();
				return orderedEnumerable;
			}
			catch (Exception e)
			{
				throw new ContainerException("Unable to get constructors for `" + registration.ConcreteType.FullName + "`", e);
			}
		}

		object Resolve(Type type, IInstanceStore tempInstanceStore, Stack<Type> buildStack)
		{
			if (tempInstanceStore != null && tempInstanceStore.ContainsInstancesFor(type))
				return tempInstanceStore.GetInstances(type).Cast<Tuple<Registration, object>>().First().Item2;

			var registrations = this.GetRegistrationsFor(type, null).ToList();

			if (registrations.Count > 1)
				throw new ContainerException("Cannot return single instance for type `" + type.GetNameForException() + "`, There are multiple instances stored.", buildStack);

			if (registrations.Count == 1)
				return this.GetOrCreateInstances(type, registrations[0].ServiceLifetime, tempInstanceStore, buildStack).First();

			var typesToCreate = this.GetRegistrationsForTypesToCreate(type, buildStack);
			return this.CreateInstance(typesToCreate.First(), tempInstanceStore, buildStack);
		}


		/// <inheritdoc />
		public T Resolve<T>()
		{
			return (T)this.Resolve(typeof(T));
		}

		/// <inheritdoc />
		public object Resolve<T>(params object[] dependencies)
		{
			return this.Resolve(typeof(T), dependencies);
		}


		object CreateInstance(Registration registration, IInstanceStore tempInstanceStore, Stack<Type> buildStack)
		{
			if (buildStack.Contains(registration.ConcreteType))
				throw new ContainerException("Cyclic dependency detected when trying to construct `" + registration.ConcreteType.GetNameForException() + "`", buildStack);

			buildStack.Push(registration.ConcreteType);

			var constructor = registration.Ctor ??
			                  (container =>
			                  {
				                  var constructors = registration.ConcreteType.GetConstructors();
				                  var ctorsWithParams = constructors.Select(c => new {ctor = c, parameters = c.GetParameters()});
				                  var orderedEnumerable = ctorsWithParams.OrderBy(x => x.parameters.Length);
				                  foreach (var ctor in orderedEnumerable)
				                  {
					                  var parameterInfos = ctor.parameters.Select(p => p.ParameterType);

					                  this.CheckDependencies(registration.ConcreteType, parameterInfos, registration.ServiceLifetime, tempInstanceStore, buildStack);

					                  var parameters = new object[ctor.parameters.Length];
					                  for (var i = 0; i < ctor.parameters.Length; i++)
					                  {
						                  var newBuildStack = new Stack<Type>(buildStack.Reverse());
						                  if (ctor.parameters[i].ParameterType.IsGenericType && ctor.parameters[i].ParameterType.GetGenericTypeDefinition() == typeof (IEnumerable<>))
						                  {
							                  var genericArgument = ctor.parameters[i].ParameterType.GetGenericArguments()[0];
							                  parameters[i] = this.ResolveAll(genericArgument, newBuildStack);
						                  }
						                  else
						                  {
							                  parameters[i] = this.Resolve(ctor.parameters[i].ParameterType, tempInstanceStore, newBuildStack);
						                  }
					                  }

					                  try
					                  {
						                  return ctor.ctor.Invoke(parameters);
					                  }
					                  catch (Exception e)
					                  {
						                  throw new ContainerException("Cannot create type `" + ctor.ctor.DeclaringType.FullName + "`", buildStack, e);
					                  }
				                  }

				                  throw new ContainerException("Unable to construct `" + registration.ConcreteType.GetNameForException() + "`", buildStack);
			                  });

			return constructor(this);
		}

		/// <summary>
		/// Trys to get an instance from the registered serviceLifetime store, creating it if it dosent exist
		/// </summary>
		/// <param name="type"></param>
		/// <param name="serviceLifetime"></param>
		/// <param name="tempInstanceStore"></param>
		/// <param name="buildStack"></param>
		/// <returns></returns>
		IEnumerable GetOrCreateInstances(Type type, ServiceLifetime serviceLifetime, IInstanceStore tempInstanceStore, Stack<Type> buildStack)
		{
			switch (serviceLifetime)
			{
				case ServiceLifetime.Singleton:
					lock (this.mutex)
						return this.GetOrCreateInstances(type, this.singletonInstanceStore, tempInstanceStore, buildStack);

				case ServiceLifetime.Scoped:
					lock (this.mutex)
						return this.GetOrCreateInstances(type, this.scopedStore, tempInstanceStore, buildStack);

				default:
					var typesToCreate = this.GetRegistrationsForTypesToCreate(type, buildStack);
					return typesToCreate.Select(typeToCreate => this.CreateInstance(typeToCreate, tempInstanceStore, buildStack)).ToArray();
			}
		}

		/// <summary>
		/// Trys to get an instance from the instance store, creating it if it doesnt exist
		/// </summary>
		/// <param name="requestType">The requested type</param>
		/// <param name="instanceStore"></param>
		/// <param name="tempInstanceStore"></param>
		/// <param name="buildStack"></param>
		/// <returns></returns>
		IEnumerable GetOrCreateInstances(Type requestType, IInstanceStore instanceStore, IInstanceStore tempInstanceStore, Stack<Type> buildStack)
		{
			var registrations = this.GetRegistrationsForTypesToCreate(requestType, buildStack);

			var instances = new List<Tuple<Registration, object>>();

			if (tempInstanceStore != null && tempInstanceStore.ContainsInstancesFor(requestType))
				instances.AddRange(tempInstanceStore.GetInstances(requestType).Cast<Tuple<Registration, object>>());
			else if (instanceStore.ContainsInstancesFor(requestType))
				instances.AddRange(instanceStore.GetInstances(requestType).Cast<Tuple<Registration, object>>());

			foreach (var registration in registrations)
			{
				// if we already have an instance from the store for this registration
				if (instances.Any(i => i != null && i.Item1 == registration))
					continue;

				var newinstance = this.CreateInstance(registration, tempInstanceStore, new Stack<Type>(buildStack.Reverse()));

				instanceStore.Insert(registration, requestType, newinstance);

				instances.Add(new Tuple<Registration, object>(registration, newinstance));
			}

			return instances.Select(i => i.Item2).ToArray();
		}


		/// <inheritdoc />
		public bool HasRegistrationsFor(Type type)
		{
			if(type == null)
				throw new ArgumentNullException(nameof(type), "Type cannot be null");

			lock (this.mutex)
			{
				if (this.transientInstanceStore.ContainsRegistrationsFor(type))
					return true;
			}

			lock (this.mutex)
			{
				if (this.singletonInstanceStore.ContainsRegistrationsFor(type))
					return true;
			}

			lock (this.mutex)
			{
				return this.scopedStore.ContainsRegistrationsFor(type);
			}
		}

		/// <inheritdoc />
		public bool HasRegistrationFor<T>()
		{
			return this.HasRegistrationsFor(typeof(T));
		}


		/// <inheritdoc />
		public IEnumerable<Registration> GetRegistrationsFor(Type type)
		{
			if (type == null)
				throw new ArgumentNullException(nameof(type), "Type cannot be null");

			return this.GetRegistrationsFor(type, null);
		}

		IEnumerable<Registration> GetRegistrationsFor(Type type, IInstanceStore tempInstanceStore)
		{
			var registrations = new List<Registration>();

			// use temp instance store first
			if (tempInstanceStore != null)
			{
				lock (tempInstanceStore)
				{
					if (tempInstanceStore.ContainsRegistrationsFor(type))
						registrations.AddRange(tempInstanceStore.GetRegistrationsFor(type));
				}
			}

			lock (this.mutex)
			{
				// TODO: send to bottom?
				registrations.AddRange(this.transientInstanceStore.GetRegistrationsFor(type));
				registrations.AddRange(this.singletonInstanceStore.GetRegistrationsFor(type));
				registrations.AddRange(this.scopedStore.GetRegistrationsFor(type));
			}

			if (registrations.Any(r => r.InjectionBehaviour == InjectionBehaviour.Override))
				return registrations.Where(r => r.InjectionBehaviour == InjectionBehaviour.Override).ToArray();

			return registrations;
		}

		/// <inheritdoc />
		public Func<IDictionary> HttpContextItemsGetter { get; set; }

		/// <inheritdoc />
		public void Register(Type abstractType, Type concreteType, ServiceLifetime lifetime = ServiceLifetime.Singleton)
		{
			if (abstractType == null)
				throw new ArgumentNullException(nameof(abstractType), "AbstractType cannot be null");
			if (concreteType == null)
				throw new ArgumentNullException(nameof(concreteType), "ConcreteType cannot be null");

			if (!concreteType.IsOrDerivesFrom(abstractType))
				throw new ContainerException("Concrete type `" + concreteType.GetNameForException() + "` is not assignable to abstract type `" + abstractType.GetNameForException() + "`");

			if (concreteType.IsInterface || concreteType.IsAbstract)
				throw new ContainerException("Concrete type `" + concreteType.GetNameForException() + "` is not a concrete type");

			var store = this.GetStore(lifetime);

			lock (this.mutex)
				store.AddRegistration(new Registration(abstractType, concreteType, null, lifetime, InjectionBehaviour.Default));
		}

		/// <inheritdoc />
		public void Register(Type abstractType, Func<IResolverContainer, object> ctor, ServiceLifetime lifetime)
		{
			if (abstractType == null)
				throw new ArgumentNullException(nameof(abstractType), "AbstractType cannot be null");

			var store = this.GetStore(lifetime);
			lock (this.mutex)
				store.AddRegistration(new Registration(abstractType, null, ctor, lifetime, InjectionBehaviour.Default));
		}

		/// <inheritdoc />
		public void Register<TConcrete>(ServiceLifetime lifetime)
		{
			this.Register(typeof(TConcrete), typeof(TConcrete), lifetime);
		}

		/// <inheritdoc />
		public void Register<TAbstract, TConcrete>(ServiceLifetime lifetime = ServiceLifetime.Singleton) where TConcrete : TAbstract
		{
			this.Register(typeof(TAbstract), typeof(TConcrete), lifetime);
		}

		/// <inheritdoc />
		public void Register<TAbstract>(Func<IResolverContainer, TAbstract> ctor, ServiceLifetime lifetime = ServiceLifetime.Singleton)
		{
			this.Register(typeof(TAbstract), c => ctor(c), lifetime);
		}


		/// <inheritdoc />
		public void Inject(object instance, Type type, ServiceLifetime lifetime, InjectionBehaviour injectionBehaviour)
		{
			if (lifetime == ServiceLifetime.Transient)
				throw new ArgumentException("You cannot inject an instance as Transient. That doesn't make sense, does it? Think about it...");

			var store = this.GetStore(lifetime);
			lock (this.mutex)
				store.Inject(type, instance, injectionBehaviour);
		}

		/// <inheritdoc />
		public void Inject<T>(T instance, ServiceLifetime lifetime = ServiceLifetime.Singleton, InjectionBehaviour injectionBehaviour = InjectionBehaviour.Default)
		{
			this.Inject(instance, typeof(T), lifetime, injectionBehaviour);
		}


		IEnumerable<Registration> GetRegistrationsForTypesToCreate(Type requestedType, Stack<Type> buildStack)
		{
			var registrations = this.GetRegistrationsFor(requestedType);
			if (registrations.Any())
				return registrations;

			if (requestedType.IsGenericType)
			{
				var genericTypeDefinition = requestedType.GetGenericTypeDefinition();

				registrations = this.GetRegistrationsFor(genericTypeDefinition);
				if (registrations.Any())
				{
					var genericArguments = requestedType.GetGenericArguments();
					registrations = this.GetRegistrationsFor(genericTypeDefinition);

					return registrations.Select(r => new Registration(requestedType, r.ConcreteType.MakeGenericType(genericArguments), null, r.ServiceLifetime, InjectionBehaviour.Default));
				}
			}

			if (!requestedType.IsAbstract && !requestedType.IsInterface)
				return new[] {new Registration(requestedType, requestedType, null, ServiceLifetime.Transient, InjectionBehaviour.Default)};

			throw new ContainerException("Cannot resolve `" + requestedType + "`, it is not constructable and has no associated registration.", buildStack);
		}

		void CheckDependencies(Type dependeeType, IEnumerable<Type> parameters, ServiceLifetime serviceLifetime, IInstanceStore tempInstanceStore, Stack<Type> buildStack)
		{
			parameters.All(p =>
			{
				if (this.CanCreateDependency(dependeeType, p, serviceLifetime, tempInstanceStore, false, buildStack))
					return true;

				if (p.IsGenericType && p.GetGenericTypeDefinition() == typeof (IEnumerable<>))
					return true;

				if (!p.IsAbstract && !p.IsInterface)
					return true;

				throw new ContainerException("Cannot create dependency `" + p.GetNameForException() + "` of dependee `" + dependeeType.GetNameForException() + "`", buildStack);
			});
		}

		bool CanCreateDependency(Type dependeeType, Type requestedType, ServiceLifetime serviceLifetime, IInstanceStore tempInstanceStore, bool allowMultiple, Stack<Type> buildStack)
		{
			var registrations = this.GetRegistrationsFor(requestedType, tempInstanceStore).ToList();
			if (registrations.Any())
			{
				if (!allowMultiple && registrations.Count > 1)
					throw new ContainerException("Cannot create dependency `" + requestedType.GetNameForException() + "`, there are multiple concrete types registered for it.", buildStack);

				if (registrations[0].ServiceLifetime.IsShorterThan(serviceLifetime))
					throw new ContainerException("Cannot create dependency `" + requestedType.GetNameForException() + "`. It's serviceLifetime (" + registrations[0].ServiceLifetime + ") is shorter than the dependee's `" + dependeeType.GetNameForException() + "` (" + serviceLifetime + ")", buildStack);

				return true;
			}

			if (requestedType.IsGenericType)
			{
				var genericTypeDefinition = requestedType.GetGenericTypeDefinition();
				return this.HasRegistrationsFor(genericTypeDefinition);
			}

			return false;
		}


		/// <inheritdoc />
		public void RemoveAllRegistrationsAndInstancesOf(Type type)
		{
			if (type == null)
				throw new ArgumentNullException(nameof(type), "Type cannot be null");

			lock (this.mutex)
				this.singletonInstanceStore.RemoveAllRegistrationsAndInstances(type);

			lock (this.mutex)
				this.scopedStore.RemoveAllRegistrationsAndInstances(type);

			lock (this.mutex)
				this.transientInstanceStore.RemoveAllRegistrationsAndInstances(type);
		}

		/// <inheritdoc />
		public void RemoveAllRegistrationsAndInstancesOf<T>()
		{
			this.RemoveAllRegistrationsAndInstancesOf(typeof(T));
		}


		/// <inheritdoc />
		public void RemoveAllInstancesWithServiceLifetime(ServiceLifetime serviceLifetime)
		{
			switch (serviceLifetime)
			{
				case ServiceLifetime.Scoped:
					this.scopedStore.RemoveAllInstances();
					break;
				case ServiceLifetime.Singleton:
					this.singletonInstanceStore.RemoveAllInstances();
					this.Inject<IContainer>(this);
					break;
				case ServiceLifetime.Transient:
					throw new ArgumentException("Can't clear transient instances, they're transient!");
			}
		}


		/// <inheritdoc />
		public void RemoveInstancesOf(Type type, ServiceLifetime serviceLifetime)
		{
			if (serviceLifetime == ServiceLifetime.Transient)
				throw new ArgumentException("You cannot remove a Transient instance. That doesn't make sense, does it? Think about it...");

			var store = this.GetStore(serviceLifetime);
			lock (this.mutex)
				store.RemoveInstances(type);
		}

		/// <inheritdoc />
		public void RemoveInstancesOf<T>(ServiceLifetime lifetime)
		{
			this.RemoveInstancesOf(typeof(T), lifetime);
		}


		/// <inheritdoc />
		public void Reset()
		{
			lock(this.mutex)
				this.scopedStore.RemoveAllRegistrationsAndInstances();

			lock(this.mutex)
				this.singletonInstanceStore.RemoveAllRegistrationsAndInstances();

			lock(this.mutex)
				this.transientInstanceStore.RemoveAllRegistrationsAndInstances();

			this.Inject<IContainer>(this);
		}


		/// <inheritdoc />
		public IEnumerable ResolveAll(Type abstractType)
		{
			if (abstractType == null)
				throw new ArgumentNullException(nameof(abstractType), "AbstractType cannot be null");

			return this.ResolveAll(abstractType, new Stack<Type>());
		}

		/// <inheritdoc />
		public IEnumerable<T> ResolveAll<T>()
		{
			return this.ResolveAll(typeof(T)).Cast<T>().ToArray();
		}

		IEnumerable ResolveAll(Type abstractType, Stack<Type> buildStack)
		{
			var instances = new List<object>();

			var registrations = this.GetRegistrationsFor(abstractType);
			foreach (var serviceLifetime in registrations.Select(r => r.ServiceLifetime).Distinct())
			{
				switch (serviceLifetime)
				{
					case ServiceLifetime.Singleton:
						lock (this.mutex)
							instances.AddRange(this.GetOrCreateInstances(abstractType, this.singletonInstanceStore, null, buildStack).Cast<object>());
						break;
					case ServiceLifetime.Scoped:
						lock (this.mutex)
							instances.AddRange(this.GetOrCreateInstances(abstractType, this.scopedStore, null, buildStack).Cast<object>());
						break;
					default:
						var typesToCreate = this.GetRegistrationsForTypesToCreate(abstractType, buildStack);
						instances.AddRange(typesToCreate.Select(typeToCreate => this.CreateInstance(typeToCreate, null, buildStack)));
						break;
				}
			}
			
			return instances.Cast(abstractType).ToArray(abstractType);
		}
		
		IInstanceStore GetStore(ServiceLifetime serviceLifetime)
		{
			switch (serviceLifetime)
			{
				case ServiceLifetime.Scoped:
					return this.scopedStore;

				case ServiceLifetime.Singleton:
					return this.singletonInstanceStore;

				case ServiceLifetime.Transient:
					return this.transientInstanceStore;

				default:
					return null;
			}
		}

		object IServiceProvider.GetService(Type serviceType)
		{
			return this.Resolve(serviceType);
		}
	}
}