﻿using System.Linq;
using System.Threading;
using Microsoft.Extensions.DependencyInjection;
using NUnit.Framework;

namespace NanoIoC.Tests
{
	[TestFixture]
	public class RemovingInstances
	{
		[Test]
		public void ShouldRemoveSingletonInstance()
		{
			var container = new Container();
			container.Inject<TestInterface>(new TestClass());
			container.RemoveInstancesOf<TestInterface>(ServiceLifetime.Singleton);

			Assert.IsFalse(container.HasRegistrationFor<TestInterface>());
		}
		
		[Test]
		public void ShouldRemoveExecutionContextInstanceOnly()
		{
			var instance1 = new TestClass();
			var instance2 = new TestClass();

			var container = new Container();
			container.Inject<TestInterface>(instance1, ServiceLifetime.Scoped);

			TestInterface[] thread2ResolvedTestClasses = null;
			bool thread2HasRegistration = true;
			ExecutionContext.SuppressFlow();
			var thread2 = new Thread(() =>
			{
				container.Inject<TestInterface>(instance2, ServiceLifetime.Scoped);

				thread2ResolvedTestClasses = container.ResolveAll<TestInterface>().ToArray();

				container.RemoveInstancesOf<TestInterface>(ServiceLifetime.Scoped);

				thread2HasRegistration = container.HasRegistrationFor<TestInterface>();
			});

			thread2.Start();
			thread2.Join(1000);
			ExecutionContext.RestoreFlow();
			Assert.IsFalse(thread2HasRegistration);
			Assert.AreEqual(1, thread2ResolvedTestClasses.Length);

			Assert.AreEqual(instance1, container.Resolve<TestInterface>());
		}


		public class TestClass : TestInterface
		{
		}

		public class TestClass2 : TestInterface
		{
		}

		public class TestClass3
		{
			public TestClass3(TestClass tc, TestClass2 tc2)
			{
			}
		}


		public interface TestInterface
		{
		}
	}
}