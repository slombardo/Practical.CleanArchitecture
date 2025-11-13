using System;

namespace ClassifiedAds.Application.Decorators.Transactional;

[AttributeUsage(AttributeTargets.Class, Inherited = false, AllowMultiple = false)]
public sealed class TransactionalAttribute : Attribute
{
}
