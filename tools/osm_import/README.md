# OpenStreetMap importer

Turns an OpenStreetMap extract plus an elevation model into the tile packs the game streams (format 3,
`docs/TILE_FORMAT.md`). Started from Dead Miles' importer.

Needs Python 3.10+ and about 8 GB of free memory for a voivodeship.

```bash
pip install osmium numpy pillow pyproj scipy shapely mapbox-earcut rasterio
```

## Building Mazowieckie

1. The extract: `data/source/mazowieckie-*.osm.pbf` (Geofabrik, `europe/poland/mazowieckie`).
2. Elevation (Poland, GUGiK NMT at 10 m, ~2 GB, restartable):

```bash
python tools/dem/fetch_gugik.py --pbf data/source/mazowieckie-261110.osm.pbf --out data/dem/gugik10
```

3. Stage 1, the extract to flat arrays (~8 minutes):

```bash
python tools/osm_import/extract.py data/source/mazowieckie-261110.osm.pbf data/work/mazowsze
```

4. Stage 2, arrays + elevation to packs in `map/`:

```bash
python tools/osm_import/build.py data/work/mazowsze map --dem data/dem/gugik10 --name mazowsze
```

`--tiles tx0,tz0,tx1,tz1` builds only those tiles (a quick test map; central Warsaw is about `32,311,34,313`).

## Coordinates

Everything is projected once, in stage 1, with the Baltic-wide Lambert conformal conic in `common.py` (D2 in
`docs/DECISIONS.md`). Any region of the Baltic loop uses the same projection, so packs from different regions fit
together with no seams. Positions are stored as int32 centimetres in stage 1 (enough for ±21,000 km) and tile-local
decimetres in the packs.

## What is in a tile

| Layer | Source | Used for |
| --- | --- | --- |
| Roads, railways, trams | `highway=*`, `railway=rail/tram` | ribbons with markings, bridges, collision; heights solved on the road graph |
| Buildings | `building=*`, `building:part=*` | extruded footprints, roofs, heights, collision |
| Land cover (4 m) | `landuse`, `natural`, `leisure`, water | ground colour, water areas |
| Ground (8 m) | elevation model | the terrain, with water beds carved |
| Water surface (8 m) | elevation over mapped water | lakes and rivers |
| Trees, lamps, poles, pylons | `natural=tree(_row)`, `highway=street_lamp`, `power=pole/tower` | street furniture |
| Points of interest | `amenity=fuel`, `shop=*`, … | (later) |
| Places | `place=city/town/village` | start places, HUD |

## Licence of the data

Map data © OpenStreetMap contributors, ODbL 1.0 (<https://www.openstreetmap.org/copyright>). The credit must stay
visible to players, and the generated packs are a derived database under the same licence. GUGiK elevation data is free
for any use.
