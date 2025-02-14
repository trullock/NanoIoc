using System;
using System.Collections;
using Microsoft.Extensions.DependencyInjection;

namespace NanoIoC
{
    public interface IContainer : IResolverContainer
    {
		// Temporary hack until we implement IServiceProvider properly
		Func<IDictionary> HttpContextItemsGetter { get; set; }

		/// <summary>
		/// Registers the given concrete type against the given abstract type with the given serviceLifetime
		/// </summary>
		/// <param name="abstractType"></param>
		/// <param name="concreteType"></param>
		/// <param name="lifetime"></param>
        void Register(Type abstractType, Type concreteType, ServiceLifetime lifetime);

		/// <summary>
		/// 
		/// </summary>
		/// <param name="abstractType"></param>
		/// <param name="ctor"></param>
		/// <param name="lifetime"></param>
		void Register(Type abstractType, Func<IResolverContainer, object> ctor, ServiceLifetime lifetime);

		/// <summary>
		/// 
		/// </summary>
		/// <typeparam name="TAbstract"></typeparam>
		/// <param name="ctor"></param>
		/// <param name="lifetime"></param>
		void Register<TAbstract>(Func<IResolverContainer, TAbstract> ctor, ServiceLifetime lifetime = ServiceLifetime.Singleton);

		/// <summary>
		/// Registers a concrete type for an abstract type
		/// </summary>
		/// <typeparam name="TAbstract">The abstract type you want to resolve later</typeparam>
		/// <typeparam name="TConcrete">The concrete type implementing the abstract type</typeparam>
		/// <param name="lifetime"></param>
		void Register<TAbstract, TConcrete>(ServiceLifetime lifetime = ServiceLifetime.Singleton) where TConcrete : TAbstract;

		/// <summary>
		/// Registers a concrete type with a serviceLifetime.
		/// You should only be using this for Singleton or HttpContextOrThreadLocal serviceLifetimes.
		/// Registering a concrete type as Transient is pointless, as you get this behaviour by default.
		/// </summary>
		/// <typeparam name="TConcrete">The concrete type to register</typeparam>
		/// <param name="lifetime">The serviceLifetime of the instance</param>
		void Register<TConcrete>(ServiceLifetime lifetime);


		/// <summary>
		/// Injects the given instance as the given type with the given serviceLifetime
		/// </summary>
		/// <param name="instance"></param>
		/// <param name="type"></param>
		/// <param name="lifetime">Must not be Transient</param>
		/// <param name="injectionBehaviour"></param>
		void Inject(object instance, Type type, ServiceLifetime lifetime, InjectionBehaviour injectionBehaviour);

		/// <summary>
		/// Injects an instance 
		/// </summary>
		/// <typeparam name="T"></typeparam>
		/// <param name="instance"></param>
		/// <param name="lifetime"></param>
		/// <param name="injectionBehaviour"></param>
		void Inject<T>(T instance, ServiceLifetime lifetime = ServiceLifetime.Singleton, InjectionBehaviour injectionBehaviour = InjectionBehaviour.Default);

		/// <summary>
		/// 
		/// </summary>
		/// <param name="type"></param>
		void RemoveAllRegistrationsAndInstancesOf(Type type);

		/// <summary>
		/// 
		/// </summary>
		/// <typeparam name="T"></typeparam>
		void RemoveAllRegistrationsAndInstancesOf<T>();

		/// <summary>
		/// Removes all instances with the given serviceLifetime
		/// </summary>
		/// <param name="serviceLifetime"></param>
		void RemoveAllInstancesWithServiceLifetime(ServiceLifetime serviceLifetime);

		/// <summary>
		/// Empty the container of everything! (apart from itself)
		/// </summary>
		void Reset();

		/// <summary>
		/// 
		/// </summary>
		/// <param name="type"></param>
		/// <param name="serviceLifetime"></param>
	    void RemoveInstancesOf(Type type, ServiceLifetime serviceLifetime);

		/// <summary>
		/// 
		/// </summary>
		/// <typeparam name="T"></typeparam>
		/// <param name="lifetime"></param>
		void RemoveInstancesOf<T>(ServiceLifetime lifetime);
    }
}