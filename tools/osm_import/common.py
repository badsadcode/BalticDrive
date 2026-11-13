"""Shared tables for the Baltic Drive OpenStreetMap importer."""
import re

import numpy as np
from pyproj import Transformer

# One map projection for the whole Baltic loop (D2 in docs/DECISIONS.md): Lambert conformal conic with standard
# parallels 53 and 67 N, centred on 58 N 20 E. Scale stays within about 1 % from the Tatras (49 N) to northern
# Finland (70 N). Game X = east, game Z = -north, metres. The same string is written into every map index and
# reproduced in Game/Core/Projection.cs.
WORLD_PROJ = '+proj=lcc +lat_1=53 +lat_2=67 +lat_0=58 +lon_0=20 +x_0=0 +y_0=0 +ellps=WGS84 +units=m +no_defs'

_tm = Transformer.from_crs('EPSG:4326', WORLD_PROJ, always_xy=True)
_inv = Transformer.from_crs(WORLD_PROJ, 'EPSG:4326', always_xy=True)


def project(lons, lats):
    """WGS84 degrees -> world metres east / north of the projection centre."""
    return _tm.transform(lons, lats)


def unproject(x, y):
    """World metres east / north -> WGS84 degrees (lon, lat)."""
    return _inv.transform(x, y)


_num = re.compile(r'^\s*(-?\d+(?:[.,]\d+)?)')


def parse_float(s):
    if not s:
        return None
    m = _num.match(s)
    if not m:
        return None
    try:
        return float(m.group(1).replace(',', '.'))
    except ValueError:
        return None


# ---- roads -----------------------------------------------------------------------------------
# class id (must match MapRoadClass in scripts/world/map/MapData.cs), default carriageway width (m)
ROAD_CLASS = {
    # Town streets are a little wider than their real carriageway: in the game there are no pavements or parked
    # cars around them and a chase camera behind, and 2.75 m lanes felt tight (D18).
    'motorway': (0, 11.0), 'trunk': (1, 9.0), 'primary': (2, 7.5), 'secondary': (3, 7.0),
    'tertiary': (4, 7.0), 'unclassified': (5, 6.5), 'residential': (6, 6.5),
    'living_street': (7, 5.5), 'service': (8, 4.0), 'track': (9, 3.0), 'pedestrian': (7, 5.5),
    'motorway_link': (10, 6.0), 'trunk_link': (10, 6.0), 'primary_link': (10, 6.0),
    'secondary_link': (10, 5.5), 'tertiary_link': (10, 5.5),
}
RAIL = {'rail': 11, 'tram': 12}

# ---- land cover ------------------------------------------------------------------------------
# raster classes (must match MapLand in MapData.cs); later entries in LAND_ORDER paint over earlier
L_OPEN, L_FOREST, L_FIELD, L_MEADOW, L_WATER, L_RESIDENTIAL, L_PAVED, L_SCRUB, L_WETLAND, L_SAND, \
    L_ORCHARD, L_PARK, L_CEMETERY, L_FARMYARD = range(14)
LAND_ORDER = [L_RESIDENTIAL, L_FIELD, L_MEADOW, L_FARMYARD, L_PAVED, L_ORCHARD, L_PARK, L_CEMETERY,
              L_SAND, L_SCRUB, L_WETLAND, L_FOREST, L_WATER]

_LANDUSE = {
    'forest': L_FOREST, 'farmland': L_FIELD, 'meadow': L_MEADOW, 'grass': L_MEADOW,
    'village_green': L_PARK, 'recreation_ground': L_PARK,
    'residential': L_RESIDENTIAL, 'industrial': L_PAVED, 'commercial': L_PAVED, 'retail': L_PAVED,
    'railway': L_PAVED, 'construction': L_PAVED, 'garages': L_PAVED, 'depot': L_PAVED,
    'orchard': L_ORCHARD, 'allotments': L_ORCHARD, 'vineyard': L_ORCHARD, 'plant_nursery': L_ORCHARD,
    'cemetery': L_CEMETERY, 'farmyard': L_FARMYARD, 'reservoir': L_WATER, 'basin': L_WATER,
    'quarry': L_SAND, 'landfill': L_SAND, 'brownfield': L_SCRUB, 'greenfield': L_MEADOW,
    'military': None, 'education': L_RESIDENTIAL, 'religious': L_PARK,
}
_NATURAL = {
    'wood': L_FOREST, 'water': L_WATER, 'scrub': L_SCRUB, 'heath': L_SCRUB, 'grassland': L_MEADOW,
    'wetland': L_WETLAND, 'sand': L_SAND, 'beach': L_SAND, 'bare_rock': L_SAND, 'shingle': L_SAND,
}
_LEISURE = {'park': L_PARK, 'garden': L_PARK, 'pitch': L_MEADOW, 'golf_course': L_PARK,
            'playground': L_PARK, 'nature_reserve': None}


def LANDUSE(tags):
    v = tags.get('natural')
    if v in _NATURAL:
        return _NATURAL[v]
    v = tags.get('landuse')
    if v in _LANDUSE:
        return _LANDUSE[v]
    v = tags.get('waterway')
    if v in ('riverbank', 'dock'):
        return L_WATER
    v = tags.get('leisure')
    if v in _LEISURE:
        return _LEISURE[v]
    v = tags.get('amenity')
    if v in ('parking', 'fuel', 'marketplace', 'bus_station'):
        return L_PAVED
    if v in ('grave_yard',):
        return L_CEMETERY
    if v in ('school', 'kindergarten', 'university', 'college', 'hospital'):
        return L_RESIDENTIAL
    return None


# ---- buildings -------------------------------------------------------------------------------
B_GENERIC, B_HOUSE, B_APARTMENTS, B_COMMERCIAL, B_INDUSTRIAL, B_FARM, B_SHED, B_CHURCH, B_CIVIC, \
    B_TOWER, B_CANOPY = range(11)

_BUILDING = {
    'house': B_HOUSE, 'detached': B_HOUSE, 'semidetached_house': B_HOUSE, 'terrace': B_HOUSE,
    'bungalow': B_HOUSE, 'residential': B_HOUSE, 'cabin': B_HOUSE, 'farm': B_HOUSE,
    'allotment_house': B_SHED, 'static_caravan': B_SHED,
    'apartments': B_APARTMENTS, 'dormitory': B_APARTMENTS, 'hotel': B_APARTMENTS,
    'commercial': B_COMMERCIAL, 'retail': B_COMMERCIAL, 'office': B_COMMERCIAL,
    'supermarket': B_COMMERCIAL, 'kiosk': B_SHED, 'service': B_SHED,
    'industrial': B_INDUSTRIAL, 'warehouse': B_INDUSTRIAL, 'manufacture': B_INDUSTRIAL,
    'hangar': B_INDUSTRIAL, 'transformer_tower': B_SHED,
    'barn': B_FARM, 'farm_auxiliary': B_FARM, 'cowshed': B_FARM, 'stable': B_FARM, 'sty': B_FARM,
    'greenhouse': B_FARM, 'outbuilding': B_SHED,
    'garage': B_SHED, 'garages': B_SHED, 'shed': B_SHED, 'hut': B_SHED, 'carport': B_CANOPY,
    'roof': B_CANOPY, 'ruins': B_SHED, 'toilets': B_SHED,
    'church': B_CHURCH, 'chapel': B_CHURCH, 'cathedral': B_CHURCH, 'religious': B_CHURCH,
    'monastery': B_CHURCH, 'synagogue': B_CHURCH, 'mosque': B_CHURCH,
    'school': B_CIVIC, 'university': B_CIVIC, 'college': B_CIVIC, 'kindergarten': B_CIVIC,
    'hospital': B_CIVIC, 'public': B_CIVIC, 'civic': B_CIVIC, 'government': B_CIVIC,
    'train_station': B_CIVIC, 'fire_station': B_CIVIC, 'sports_hall': B_CIVIC, 'stadium': B_CIVIC,
    'museum': B_CIVIC, 'theatre': B_CIVIC, 'palace': B_CIVIC, 'castle': B_CIVIC,
    'tower': B_TOWER, 'water_tower': B_TOWER, 'chimney': B_TOWER, 'silo': B_TOWER,
}

# used when the map gives neither a height nor a storey count (metres to the top of the roof)
BUILDING_DEFAULT_HEIGHT = {
    B_GENERIC: 6.0, B_HOUSE: 7.5, B_APARTMENTS: 15.0, B_COMMERCIAL: 6.5, B_INDUSTRIAL: 8.5,
    B_FARM: 6.5, B_SHED: 3.0, B_CHURCH: 16.0, B_CIVIC: 10.0, B_TOWER: 22.0, B_CANOPY: 5.2,
}

# must match MapRoof in MapData.cs
ROOF_SHAPES = {'flat': 1, 'gabled': 2, 'hipped': 3, 'pyramidal': 4, 'skillion': 1, 'dome': 5,
               'onion': 5, 'round': 2, 'half-hipped': 2, 'gambrel': 2, 'mansard': 3, 'cone': 4,
               'saltbox': 2}


def building_type(kind, tags):
    t = _BUILDING.get(kind)
    if t is not None:
        return t
    if tags.get('amenity') == 'place_of_worship':
        return B_CHURCH
    if tags.get('shop') or tags.get('office'):
        return B_COMMERCIAL
    if tags.get('man_made') in ('tower', 'chimney', 'water_tower', 'silo'):
        return B_TOWER
    return B_GENERIC


# ---- points of interest ----------------------------------------------------------------------
# must match MapPoiKind in MapData.cs
P_FUEL, P_FOOD, P_SHOP, P_MEDICAL, P_DINER, P_GARAGE, P_POLICE, P_HARDWARE, P_CAMP = range(9)

_SHOP_FOOD = {'supermarket', 'convenience', 'bakery', 'butcher', 'greengrocer', 'alcohol',
              'beverages', 'deli', 'general', 'kiosk', 'confectionery', 'farm'}
_SHOP_HARDWARE = {'hardware', 'doityourself', 'trade', 'agrarian', 'building_materials',
                  'outdoor', 'hunting', 'fishing', 'tool_hire'}
_SHOP_GARAGE = {'car_repair', 'car_parts', 'tyres', 'car', 'motorcycle', 'bicycle'}
_SHOP_MEDICAL = {'chemist', 'medical_supply', 'optician'}


def poi_kind(tags):
    a = tags.get('amenity')
    if a == 'fuel':
        return P_FUEL
    if a in ('pharmacy', 'clinic', 'hospital', 'doctors', 'veterinary'):
        return P_MEDICAL
    if a in ('restaurant', 'fast_food', 'cafe', 'bar', 'pub', 'food_court'):
        return P_DINER
    if a in ('police', 'fire_station'):
        return P_POLICE
    if a == 'car_wash':
        return P_GARAGE
    s = tags.get('shop')
    if s:
        if s in _SHOP_FOOD:
            return P_FOOD
        if s in _SHOP_HARDWARE:
            return P_HARDWARE
        if s in _SHOP_GARAGE:
            return P_GARAGE
        if s in _SHOP_MEDICAL:
            return P_MEDICAL
        if s not in ('vacant', 'no'):
            return P_SHOP
    t = tags.get('tourism')
    if t in ('camp_site', 'picnic_site', 'wilderness_hut'):
        return P_CAMP
    return None


# small point features (must match MapPointKind in MapData.cs)
PT_TREE, PT_LAMP, PT_POLE, PT_PYLON = range(4)


def point_kind(tags):
    if tags.get('natural') == 'tree':
        return PT_TREE
    if tags.get('highway') == 'street_lamp':
        return PT_LAMP
    p = tags.get('power')
    if p == 'pole':
        return PT_POLE
    if p == 'tower':
        return PT_PYLON
    return None


PLACE_RANK = {'city': 0, 'town': 1, 'village': 2, 'suburb': 3, 'hamlet': 4}
