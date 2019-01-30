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

		internal static IEnumerable<IContainerRegistry> Registries;
		internal static IEnumerable<ITypeProcessor> TypeProcessors;

		/// <summary>
		/// Global container instance
		/// </summary>
		[Obsolete("Do not use, this is an antipattern")]
		public static readonly IContainer Global;

		static Container()
		{
			Global = new Container();
			FindAllRegistriesAndTypeProcessors();
		}

		static void FindAllRegistriesAndTypeProcessors()
		{
			var registries = new List<IContainerRegistry>();
			var typeProcessors = new List<ITypeProcessor>();

			var assemblies = Assemblies.AllFromApplicationBaseDirectory(a => !a.FullName.StartsWith("System"));
			foreach (var assembly in assemblies)
			{
				Type[] types;

				try
				{
					types = assembly.GetTypes();
				}
				catch (TypeInitializationException e)
				{
					if (e.InnerException is ReflectionTypeLoadException inner)
						throw new ContainerException(inner.LoaderExceptions, e);
					throw;
				}
				catch (ReflectionTypeLoadException e)
				{
					throw new ContainerException(e.LoaderExceptions, e);
				}

				foreach (var type in types)
				{
					if (typeof (IContainerRegistry).IsAssignableFrom(type) && !type.IsInterface && !type.IsAbstract && !type.ContainsGenericParameters)
						registries.Add(Activator.CreateInstance(type) as IContainerRegistry);

					if (typeof (ITypeProcessor).IsAssignableFrom(type) && !type.IsInterface && !type.IsAbstract && !type.ContainsGenericParameters)
						typeProcessors.Add(Activator.CreateInstance(type) as ITypeProcessor);
				}
			}

			TypeProcessors = typeProcessors;
			Registries = registries;
		}

		public Container()
		{
			this.singletonInstanceStore = new SingletonInstanceStore();
			this.scopedStore = new ScopedInstanceStore();
			this.transientInstanceStore = new TransientInstanceStore();

			this.Inject<IContainer>(this);
			this.Inject<IResolverContainer>(this);
		}

		internal Container(Container container)
		{
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

		/// <summary>
		/// Returns the dependency graph for the given type
		/// </summary>
		/// <param name="type"></param>
		/// <returns></returns>
		public GraphNode DependencyGraph(Type type)
		{
			return this.DependencyGraph_Visit(type, new Stack<Type>());
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

		private static IEnumerable<TypeCtor> GetConstructors(Registration registration)
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
					lock (this.singletonInstanceStore.Mutex)
						return this.GetOrCreateInstances(type, this.singletonInstanceStore, tempInstanceStore, buildStack);

				case ServiceLifetime.Scoped:
					lock (this.scopedStore.Mutex)
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

		/// <summary>
		/// Determines if there is a registration for the requested type
		/// </summary>
		/// <param name="type"></param>
		/// <returns></returns>
		public bool HasRegistrationsFor(Type type)
		{
			if(type == null)
				throw new ArgumentNullException(nameof(type), "Type cannot be null");

			lock (this.transientInstanceStore.Mutex)
			{
				if (this.transientInstanceStore.ContainsRegistrationsFor(type))
					return true;
			}

			lock (this.singletonInstanceStore.Mutex)
			{
				if (this.singletonInstanceStore.ContainsRegistrationsFor(type))
					return true;
			}

			lock (this.scopedStore.Mutex)
			{
				return this.scopedStore.ContainsRegistrationsFor(type);
			}
		}

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
				lock (tempInstanceStore.Mutex)
				{
					if (tempInstanceStore.ContainsRegistrationsFor(type))
						registrations.AddRange(tempInstanceStore.GetRegistrationsFor(type));
				}
			}

			// TODO: send to bottom?
			lock (this.transientInstanceStore.Mutex)
				registrations.AddRange(this.transientInstanceStore.GetRegistrationsFor(type));

			lock (this.singletonInstanceStore.Mutex)
				registrations.AddRange(this.singletonInstanceStore.GetRegistrationsFor(type));

			lock (this.scopedStore.Mutex)
				registrations.AddRange(this.scopedStore.GetRegistrationsFor(type));

			if (registrations.Any(r => r.InjectionBehaviour == InjectionBehaviour.Override))
				return registrations.Where(r => r.InjectionBehaviour == InjectionBehaviour.Override).ToArray();

			return registrations;
		}

		public void Register(Type abstractType, Type concreteType, ServiceLifetime serviceLifetime = ServiceLifetime.Singleton)
		{
			if (abstractType == null)
				throw new ArgumentNullException(nameof(abstractType), "AbstractType cannot be null");
			if (concreteType == null)
				throw new ArgumentNullException(nameof(concreteType), "ConcreteType cannot be null");

			if (!concreteType.IsOrDerivesFrom(abstractType))
				throw new ContainerException("Concrete type `" + concreteType.GetNameForException() + "` is not assignable to abstract type `" + abstractType.GetNameForException() + "`");

			if (concreteType.IsInterface || concreteType.IsAbstract)
				throw new ContainerException("Concrete type `" + concreteType.GetNameForException() + "` is not a concrete type");

			var store = this.GetStore(serviceLifetime);

			lock (store.Mutex)
				store.AddRegistration(new Registration(abstractType, concreteType, null, serviceLifetime, InjectionBehaviour.Default));
		}

		public void Register(Type abstractType, Func<IResolverContainer, object> ctor, ServiceLifetime serviceLifetime)
		{
			if (abstractType == null)
				throw new ArgumentNullException(nameof(abstractType), "AbstractType cannot be null");

			var store = this.GetStore(serviceLifetime);
			lock (store.Mutex)
				store.AddRegistration(new Registration(abstractType, null, ctor, serviceLifetime, InjectionBehaviour.Default));
		}

		public void Inject(object instance, Type type, ServiceLifetime lifeCycle, InjectionBehaviour injectionBehaviour)
		{
			if (lifeCycle == ServiceLifetime.Transient)
				throw new ArgumentException("You cannot inject an instance as Transient. That doesn't make sense, does it? Think about it...");

			var store = this.GetStore(lifeCycle);
			lock (store.Mutex)
				store.Inject(type, instance, injectionBehaviour);
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

		public void RemoveAllRegistrationsAndInstancesOf(Type type)
		{
			if (type == null)
				throw new ArgumentNullException(nameof(type), "Type cannot be null");

			lock (this.singletonInstanceStore.Mutex)
				this.singletonInstanceStore.RemoveAllRegistrationsAndInstances(type);

			lock (this.scopedStore.Mutex)
				this.scopedStore.RemoveAllRegistrationsAndInstances(type);

			lock (this.transientInstanceStore.Mutex)
				this.transientInstanceStore.RemoveAllRegistrationsAndInstances(type);
		}

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

		public void RemoveInstancesOf(Type type, ServiceLifetime serviceLifetime)
		{
			if (serviceLifetime == ServiceLifetime.Transient)
				throw new ArgumentException("You cannot remove a Transient instance. That doesn't make sense, does it? Think about it...");

			var store = this.GetStore(serviceLifetime);
			lock (store.Mutex)
				store.RemoveInstances(type);
		}

		public void Reset()
		{
			lock(this.scopedStore.Mutex)
				this.scopedStore.RemoveAllRegistrationsAndInstances();

			lock(this.singletonInstanceStore.Mutex)
				this.singletonInstanceStore.RemoveAllRegistrationsAndInstances();

			lock(this.transientInstanceStore.Mutex)
				this.transientInstanceStore.RemoveAllRegistrationsAndInstances();

			this.Inject<IContainer>(this);
		}

		public IEnumerable ResolveAll(Type abstractType)
		{
			if (abstractType == null)
				throw new ArgumentNullException(nameof(abstractType), "AbstractType cannot be null");

			return this.ResolveAll(abstractType, new Stack<Type>());
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
						lock (this.singletonInstanceStore.Mutex)
							instances.AddRange(this.GetOrCreateInstances(abstractType, this.singletonInstanceStore, null, buildStack).Cast<object>());
						break;
					case ServiceLifetime.Scoped:
						lock (this.scopedStore.Mutex)
							instances.AddRange(this.GetOrCreateInstances(abstractType, this.scopedStore, null, buildStack).Cast<object>());
						break;
					default:
						var typesToCreate = this.GetRegistrationsForTypesToCreate(abstractType, buildStack);
						instances.AddRange(typesToCreate.Select(typeToCreate => this.CreateInstance(typeToCreate, null, buildStack)));
						break;
				}
			}

			return instances.Cast(abstractType);
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