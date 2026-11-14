using System.Reflection;
using System.Runtime.CompilerServices;
using Urlog.Core.Metadata;

namespace Urlog.Api.Tests;

/// <summary>
/// The API changes a case's state through <see cref="LifecycleOperations"/> only. The transition table by
/// itself checks no role and no precondition, so the API assembly must not be able to call it.
/// </summary>
public sealed class LifecycleBoundaryTests
{
    [Fact]
    public void TheApiAssemblyCannotReachTheBareTransition()
    {
        var core = typeof(CaseOperations).Assembly;
        var api = typeof(UrlogOptions).Assembly;
        var bare = typeof(CaseOperations).GetMethods(BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic).Where(m => m.Name == "TransitionAsync").ToList();
        Assert.NotEmpty(bare);
        // Internal to Core: not public, not protected, and Core opens its internals to test assemblies only.
        Assert.All(bare, method => Assert.True(method.IsAssembly, $"{method} is reachable from outside {core.GetName().Name}"));
        var friends = core.GetCustomAttributes<InternalsVisibleToAttribute>().Select(a => a.AssemblyName.Split(',')[0].Trim()).ToList();
        Assert.DoesNotContain(api.GetName().Name, friends);
        Assert.All(friends, friend => Assert.True(friend.EndsWith("Tests", StringComparison.Ordinal) || friend == "Bench", $"{core.GetName().Name} opens its internals to {friend}"));

        // The row itself cannot be moved from outside either: the state and what a transition stamps have no public setter.
        foreach (var name in new[] { nameof(CaseRecord.State), nameof(CaseRecord.PriorState), nameof(CaseRecord.DeletedAt), nameof(CaseRecord.PurgingAt) })
        {
            var setter = typeof(CaseRecord).GetProperty(name)!.SetMethod;
            Assert.True(setter is null || setter.IsAssembly, $"CaseRecord.{name} can be set from outside {core.GetName().Name}");
        }

        // And nothing else public takes a target state and changes anything: the two that are left only answer.
        var takers = core.GetExportedTypes()
            .SelectMany(t => t.GetMethods(BindingFlags.Static | BindingFlags.Instance | BindingFlags.Public | BindingFlags.DeclaredOnly))
            .Where(m => !m.IsSpecialName && m.GetParameters().Any(p => p.ParameterType == typeof(CaseState)))
            .Select(m => $"{m.DeclaringType!.Name}.{m.Name}").Distinct().Order().ToList();
        Assert.Equal(["CaseLifecycle.CanTransition", "CaseLifecycle.Operation"], takers);
    }
}
