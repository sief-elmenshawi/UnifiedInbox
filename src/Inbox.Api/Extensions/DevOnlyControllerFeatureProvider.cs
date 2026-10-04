using Inbox.Api.Controllers;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.ApplicationParts;
using Microsoft.AspNetCore.Mvc.Controllers;

namespace Inbox.Api.Extensions;

/// <summary>
/// Removes the dev-only controllers from MVC's controller list when the environment is not
/// Development, so <c>/api/dev/*</c> does not exist outside a developer machine - it is not merely
/// rejected at runtime.
/// </summary>
public sealed class DevOnlyControllerFeatureProvider(IHostEnvironment environment)
    : IApplicationFeatureProvider<ControllerFeature>
{
    public void PopulateFeature(IEnumerable<ApplicationPart> parts, ControllerFeature feature)
    {
        if (environment.IsDevelopment())
        {
            return;
        }

        var controllers = feature.Controllers
            .Where(IsDevOnly)
            .ToList();

        foreach (var controller in controllers)
        {
            feature.Controllers.Remove(controller);
        }
    }

    private static bool IsDevOnly(Type controllerType) =>
        typeof(DevController).IsAssignableFrom(controllerType);
}