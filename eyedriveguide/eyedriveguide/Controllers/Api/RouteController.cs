using EyeDriveGuide.Services;
using Microsoft.AspNetCore.Mvc;

namespace EyeDriveGuide.Controllers.Api
{
    [ApiController]
    [Route("api/route")]
    public class RouteController : ControllerBase
    {
        private readonly RouteService _routeService;

        public RouteController(RouteService routeService)
        {
            _routeService = routeService;
        }

        [HttpGet("load")]
        public async Task<IActionResult> Load(
            [FromQuery] double startLat, [FromQuery] double startLng,
            [FromQuery] double endLat, [FromQuery] double endLng)
        {
            var graph = await _routeService.LoadRouteAsync(startLat, startLng, endLat, endLng);
            return Ok(graph);
        }
    }
}
