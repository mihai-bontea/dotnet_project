using System.Collections.Concurrent;
using System.Collections.Immutable;
using System.Runtime.InteropServices;
using System.Runtime.Serialization;
using CommandLine;
using Mapster.Common;
using Mapster.Common.MemoryMappedTypes;
using OSMDataParser;
using OSMDataParser.Elements;

namespace MapFeatureGenerator;

public static class Program
{
    private static MapData LoadOsmFile(ReadOnlySpan<char> osmFilePath)
    {
        var nodes = new ConcurrentDictionary<long, AbstractNode>();
        var ways = new ConcurrentBag<Way>();

        Parallel.ForEach(new PBFFile(osmFilePath), (blob, _) =>
        {
            switch (blob.Type)
            {
                case BlobType.Primitive:
                    var primitiveBlock = blob.ToPrimitiveBlock();
                    foreach (var primitiveGroup in primitiveBlock)
                        switch (primitiveGroup.ContainedType)
                        {
                            case PrimitiveGroup.ElementType.Node:
                                foreach (var node in primitiveGroup) nodes[node.Id] = (AbstractNode)node;
                                break;

                            case PrimitiveGroup.ElementType.Way:
                                foreach (var way in primitiveGroup) ways.Add((Way)way);
                                break;
                        }

                    break;
            }
        });

        var tiles = new Dictionary<int, List<long>>();
        foreach (var (id, node) in nodes)
        {
            var tileId = TiligSystem.GetTile(new Coordinate(node.Latitude, node.Longitude));
            if (tiles.TryGetValue(tileId, out var nodeIds))
            {
                nodeIds.Add(id);
            }
            else
            {
                tiles[tileId] = new List<long>
                {
                    id
                };
            }
        }

        return new MapData
        {
            Nodes = nodes.ToImmutableDictionary(),
            Tiles = tiles.ToImmutableDictionary(),
            Ways = ways.ToImmutableArray()
        };
    }

    private static void CreateMapDataFile(ref MapData mapData, string filePath)
    {
        var usedNodes = new HashSet<long>();

        var featureIds = new List<long>();
        // var geometryTypes = new List<GeometryType>();
        // var coordinates = new List<(long id, (int offset, List<Coordinate> coordinates) values)>();

        var labels = new List<int>();
        // var propKeys = new List<(long id, (int offset, IEnumerable<string> keys) values)>();
        // var propValues = new List<(long id, (int offset, IEnumerable<string> values) values)>();

        using var fileWriter = new BinaryWriter(File.OpenWrite(filePath));
        var offsets = new Dictionary<int, long>(mapData.Tiles.Count);

        // Write FileHeader
        fileWriter.Write((long)1); // FileHeader: Version
        fileWriter.Write(mapData.Tiles.Count); // FileHeader: TileCount

        // Write TileHeaderEntry
        foreach (var tile in mapData.Tiles)
        {
            fileWriter.Write(tile.Key); // TileHeaderEntry: ID
            fileWriter.Write((long)0); // TileHeaderEntry: OffsetInBytes
        }

        foreach (var (tileId, _) in mapData.Tiles)
        {
            usedNodes.Clear();

            featureIds.Clear();
            labels.Clear();

            var totalCoordinateCount = 0;
            var totalPropertyCount = 0;

            var featuresData = new Dictionary<long, FeatureData>();

            foreach (var way in mapData.Ways)
            {
                var featureData = new FeatureData
                {
                    Id = way.Id,
                    Coordinates = (totalCoordinateCount, new List<Coordinate>()),
                    PropertyKeys = (totalPropertyCount, new List<string>(way.Tags.Count)),
                    PropertyValues = (totalPropertyCount, new List<string>(way.Tags.Count))
                };

                featureIds.Add(way.Id);
                var geometryType = GeometryType.Polyline;

                labels.Add(-1);
                foreach (var tag in way.Tags)
                {
                    if (tag.Key == "name")
                    {
                        labels[^1] = totalPropertyCount * 2 + featureData.PropertyKeys.keys.Count * 2 + 1;
                    }
                    featureData.PropertyKeys.keys.Add(tag.Key);
                    featureData.PropertyValues.values.Add(tag.Value);
                }

                foreach (var nodeId in way.NodeIds)
                {
                    var node = mapData.Nodes[nodeId];
                    usedNodes.Add(nodeId);

                    foreach (var (key, value) in node.Tags)
                    {
                        if (!featureData.PropertyKeys.keys.Contains(key))
                        {
                            featureData.PropertyKeys.keys.Add(key);
                            featureData.PropertyValues.values.Add(value);
                        }
                    }

                    featureData.Coordinates.coordinates.Add(new Coordinate(node.Latitude, node.Longitude));
                }

                if (featureData.Coordinates.coordinates[0] == featureData.Coordinates.coordinates[^1])
                {
                    geometryType = GeometryType.Polygon;
                }
                featureData.GeometryType = (byte)geometryType;

                totalPropertyCount += featureData.PropertyKeys.keys.Count;
                totalCoordinateCount += featureData.Coordinates.coordinates.Count;

                if (featureData.PropertyKeys.keys.Count != featureData.PropertyValues.values.Count)
                {
                    throw new InvalidDataContractException("Property keys and values should have the same count");
                }

                featuresData.Add(way.Id, featureData);
            }

            foreach (var (nodeId, node) in mapData.Nodes.Where(n => !usedNodes.Contains(n.Key)))
            {
                featureIds.Add(nodeId);

                var featurePropKeys = new List<string>();
                var featurePropValues = new List<string>();

                labels.Add(-1);
                for (var i = 0; i < node.Tags.Count; ++i)
                {
                    var tag = node.Tags[i];
                    if (tag.Key == "name")
                    {
                        labels[^1] = totalPropertyCount * 2 + featurePropKeys.Count * 2 + 1;
                    }

                    featurePropKeys.Add(tag.Key);
                    featurePropValues.Add(tag.Value);
                }

                if (featurePropKeys.Count != featurePropValues.Count)
                {
                    throw new InvalidDataContractException("Property keys and values should have the same count");
                }

                featuresData.Add(nodeId, new FeatureData
                {
                    Id = nodeId,
                    GeometryType = (byte)GeometryType.Point,
                    Coordinates = (totalCoordinateCount, new List<Coordinate>
                    {
                        new Coordinate(node.Latitude, node.Longitude)
                    }),
                    PropertyKeys = (totalPropertyCount, featurePropKeys),
                    PropertyValues = (totalPropertyCount, featurePropValues)
                });

                totalPropertyCount += featurePropKeys.Count;
                ++totalCoordinateCount;
            }

            offsets.Add(tileId, fileWriter.BaseStream.Position);

            // Write TileBlockHeader
            fileWriter.Write(featureIds.Count); // TileBlockHeader: FeatureCount
            fileWriter.Write(totalCoordinateCount); // TileBlockHeader: CoordinateCount
            fileWriter.Write(totalPropertyCount * 2); // TileBlockHeader: StringCount
            fileWriter.Write(0); //TileBlockHeader: CharactersCount

            // Take note of the offset within the file for this field
            var coPosition = fileWriter.BaseStream.Position;
            // Write a placeholder value to reserve space in the file
            fileWriter.Write((long)0); // TileBlockHeader: CoordinatesOffsetInBytes (placeholder)

            // Take note of the offset within the file for this field
            var soPosition = fileWriter.BaseStream.Position;
            // Write a placeholder value to reserve space in the file
            fileWriter.Write((long)0); // TileBlockHeader: StringsOffsetInBytes (placeholder)

            // Take note of the offset within the file for this field
            var choPosition = fileWriter.BaseStream.Position;
            // Write a placeholder value to reserve space in the file
            fileWriter.Write((long)0); // TileBlockHeader: CharactersOffsetInBytes (placeholder)

            // Write MapFeatures
            for (var i = 0; i < featureIds.Count; ++i)
            {
                var featureData = featuresData[featureIds[i]];

                fileWriter.Write(featureIds[i]); // MapFeature: Id
                fileWriter.Write(labels[i]); // MapFeature: LabelOffset
                fileWriter.Write(featureData.GeometryType); // MapFeature: GeometryType
                fileWriter.Write(featureData.Coordinates.offset); // MapFeature: CoordinateOffset
                fileWriter.Write(featureData.Coordinates.coordinates.Count); // MapFeature: CoordinateCount
                fileWriter.Write(featureData.PropertyKeys.offset * 2); // MapFeature: PropertiesOffset 
                fileWriter.Write(featureData.PropertyKeys.keys.Count); // MapFeature: PropertyCount
            }

            // Record the current position in the stream
            var currentPosition = fileWriter.BaseStream.Position;
            // Seek back in the file to the position of the field
            fileWriter.Seek((int)coPosition, SeekOrigin.Begin);
            // Write the recorded 'currentPosition'
            fileWriter.Write(currentPosition); // TileBlockHeader: CoordinatesOffsetInBytes
            // And seek forward to continue updating the file
            fileWriter.Seek((int)currentPosition, SeekOrigin.Begin);
            foreach (var t in featureIds)
            {
                var featureData = featuresData[t];

                foreach (var c in featureData.Coordinates.coordinates)
                {
                    fileWriter.Write(c.Latitude); // Coordinate: Latitude
                    fileWriter.Write(c.Longitude); // Coordinate: Longitude
                }
            }

            // Record the current position in the stream
            currentPosition = fileWriter.BaseStream.Position;
            // Seek back in the file to the position of the field
            fileWriter.Seek((int)soPosition, SeekOrigin.Begin);
            // Write the recorded 'currentPosition'
            fileWriter.Write(currentPosition); // TileBlockHeader: StringsOffsetInBytes
            // And seek forward to continue updating the file
            fileWriter.Seek((int)currentPosition, SeekOrigin.Begin);

            var stringOffset = 0;
            foreach (var t in featureIds)
            {
                var featureData = featuresData[t];
                for (var i = 0; i < featureData.PropertyKeys.keys.Count; ++i)
                {
                    // Add 1 to the offset for keys
                    string v = featureData.PropertyValues.values[i];

                    fileWriter.Write(stringOffset);
                    fileWriter.Write(1);
                    stringOffset += 1;

                    // If sub-property exists in enum, add 1 to offset
                    // otherwise, add the whole string
                    if (SubPropToEnumCode(v) != FeatureSubProp.Unknown)
                    {
                        fileWriter.Write(stringOffset);
                        fileWriter.Write(1);
                        stringOffset += 1;
                    }
                    else
                    {
                        fileWriter.Write(stringOffset);
                        fileWriter.Write(v.Length);
                        stringOffset += v.Length;
                    }
                }
            }

            // Record the current position in the stream
            currentPosition = fileWriter.BaseStream.Position;
            // Seek back in the file to the position of the field
            fileWriter.Seek((int)choPosition, SeekOrigin.Begin);
            // Write the recorded 'currentPosition'
            fileWriter.Write(currentPosition); // TileBlockHeader: CharactersOffsetInBytes
            // And seek forward to continue updating the file
            fileWriter.Seek((int)currentPosition, SeekOrigin.Begin);
            foreach (var t in featureIds)
            {
                var featureData = featuresData[t];
                for (var i = 0; i < featureData.PropertyKeys.keys.Count; ++i)
                {
                    // each 'property' (key) will be just one short in memory, reducing space occupied
                    string k = featureData.PropertyKeys.keys[i];
                    fileWriter.Write((short)PropToEnumCode(k));

                    // each 'sub-property' (value) will also be just a short in memory if the predefined
                    // enum is found. Otherwise it means it has to be some string like a name of a city, in that
                    // case we shall write the whole string. Still, memory will be saved
                    string v = featureData.PropertyValues.values[i];
                    if (SubPropToEnumCode(v) != FeatureSubProp.Unknown)
                    {
                        fileWriter.Write((short)SubPropToEnumCode(v));
                    }
                    else
                    {
                        foreach (var c in v)
                        {
                            fileWriter.Write((short)c);
                        }
                    }
                }
            }
        }

        // Seek to the beginning of the file, just before the first TileHeaderEntry
        fileWriter.Seek(Marshal.SizeOf<FileHeader>(), SeekOrigin.Begin);
        foreach (var (tileId, offset) in offsets)
        {
            fileWriter.Write(tileId);
            fileWriter.Write(offset);
        }

        fileWriter.Flush();
    }
    
    // basically the 'key' of the property
    private static FeatureProp PropToEnumCode(string propName)
    {
        var dict = new Dictionary<string, FeatureProp>
        {
            { "admin_level", FeatureProp.AdminLevel },
            { "place", FeatureProp.Place },
            { "name", FeatureProp.Name },
            { "highway", FeatureProp.Highway },
            { "water", FeatureProp.Water },
            { "railway", FeatureProp.Railway },
            { "natural", FeatureProp.Natural },
            { "boundary", FeatureProp.Boundary },
            { "landuse", FeatureProp.Landuse },
            { "building", FeatureProp.Building },
            { "amenity", FeatureProp.Amenity },
            { "leisure", FeatureProp.Leisure }
        };
        if (dict.ContainsKey(propName))
            return dict[propName];
        return FeatureProp.Unknown;
    }
    
    private static FeatureSubProp SubPropToEnumCode(string subPropName)
    {
        var dict = new Dictionary<string, FeatureSubProp>
        {
            { "motorway", FeatureSubProp.Motorway },
            { "trunk", FeatureSubProp.Trunk },
            { "primary", FeatureSubProp.Primary },
            { "secondary", FeatureSubProp.Secondary },
            { "tertiary", FeatureSubProp.Tertiary },
            { "unclassified", FeatureSubProp.Unclassified },
            { "road", FeatureSubProp.Road },
            { "natural", FeatureSubProp.Natural },
            { "fell", FeatureSubProp.Fell },
            { "grassland", FeatureSubProp.Grassland },
            { "heath", FeatureSubProp.Heath },
            { "moor", FeatureSubProp.Moor },
            { "scrub", FeatureSubProp.Scrub },
            { "wetland", FeatureSubProp.Wetland },
            { "wood", FeatureSubProp.Wood },
            { "tree_row", FeatureSubProp.TreeRow },
            { "bare_rock", FeatureSubProp.BareRock },
            { "rock", FeatureSubProp.Rock },
            { "scree", FeatureSubProp.Scree },
            { "beach", FeatureSubProp.Beach },
            { "sand", FeatureSubProp.Sand },
            { "water", FeatureSubProp.Water },
            { "2", FeatureSubProp.Two },
            { "city", FeatureSubProp.City },
            { "town", FeatureSubProp.Town },
            { "locality", FeatureSubProp.Locality },
            { "hamlet", FeatureSubProp.Hamlet },
            { "administrative", FeatureSubProp.Administrative },
            { "forest", FeatureSubProp.Forest },
            { "orchard", FeatureSubProp.Orchard },
            { "residential", FeatureSubProp.Residential },
            { "cemetery", FeatureSubProp.Cemetery },
            { "industrial", FeatureSubProp.Industrial },
            { "commercial", FeatureSubProp.Commercial },
            { "square", FeatureSubProp.Square },
            { "construction", FeatureSubProp.Construction },
            { "military", FeatureSubProp.Military },
            { "quarry", FeatureSubProp.Quarry },
            { "brownfield", FeatureSubProp.Brownfield },
            { "farm", FeatureSubProp.Farm },
            { "meadow", FeatureSubProp.Meadow },
            { "grass", FeatureSubProp.Grass },
            { "greenfield", FeatureSubProp.Greenfield },
            { "recreation_ground", FeatureSubProp.RecreationGround },
            { "winter_sports", FeatureSubProp.WinterSports },
            { "allotments", FeatureSubProp.Allotments },
            { "reservoir", FeatureSubProp.Reservoir },
            { "basin", FeatureSubProp.Basin }
        };

        if (dict.ContainsKey(subPropName))
            return dict[subPropName];

        return FeatureSubProp.Unknown;
    }

    public static void Main(string[] args)
    {
        Options? arguments = null;
        var argParseResult =
            Parser.Default.ParseArguments<Options>(args).WithParsed(options => { arguments = options; });

        if (argParseResult.Errors.Any())
        {
            Environment.Exit(-1);
        }

        var mapData = LoadOsmFile(arguments!.OsmPbfFilePath);
        CreateMapDataFile(ref mapData, arguments!.OutputFilePath!);
    }

    public class Options
    {
        [Option('i', "input", Required = true, HelpText = "Input osm.pbf file")]
        public string? OsmPbfFilePath { get; set; }

        [Option('o', "output", Required = true, HelpText = "Output binary file")]
        public string? OutputFilePath { get; set; }
    }

    private readonly struct MapData
    {
        public ImmutableDictionary<long, AbstractNode> Nodes { get; init; }
        public ImmutableDictionary<int, List<long>> Tiles { get; init; }
        public ImmutableArray<Way> Ways { get; init; }
    }

    private struct FeatureData
    {
        public long Id { get; init; }

        public byte GeometryType { get; set; }
        public (int offset, List<Coordinate> coordinates) Coordinates { get; init; }
        public (int offset, List<string> keys) PropertyKeys { get; init; }
        public (int offset, List<string> values) PropertyValues { get; init; }
    }
}
