# Captain of Industry mods

A collection of my mods for [Captain of Industry](https://www.captain-of-industry.com/),
one per subdirectory.

| Mod | Description |
| --- | --- |
| [Elevation++](ElevationPP/) | Elevated train stations, rail portals with automatic side pillars, vertical and balancing pipe/transport connectors. |
| [Shipping++](ShippingPP/) | Local cargo shipping: dockside terminals with their own ships hauling products between docks on the same island, with shipping lines and navigation buoys. |
| [Excavator++](ExcavatorPP/) | Excavators keep mining with a partly filled bucket instead of waiting for a truck. |
| [ModConfig++](ModConfigPP/) | Change any mod's settings from inside a running game. |

Each mod builds with its own `build.ps1` (MSBuild + deploy into the game's
`Mods` folder; requires the `COI_ROOT` and `COI_MODS` environment variables).

Setting up another machine (build prerequisites, environment variables, development
notes): see [docs/README.md](docs/README.md).
