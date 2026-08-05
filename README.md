# Captain of Industry mods

A collection of my mods for [Captain of Industry](https://www.captain-of-industry.com/),
one per subdirectory.

| Mod | Description |
| --- | --- |
| [Elevation++](ElevationPP/) | Elevated train stations, rail portals with automatic side pillars, vertical and balancing pipe/transport connectors. |
| [Shipping++](ShippingPP/) | Local cargo shipping: dockside terminals with their own ships hauling products between docks on the same island, with shipping lines and navigation buoys. |

Each mod builds with its own `build.ps1` (MSBuild + deploy into the game's
`Mods` folder; requires the `COI_ROOT` and `COI_MODS` environment variables).
