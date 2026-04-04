using FluentAssertions;
using ThreeDTilesLink.Core.Tiles;

namespace ThreeDTilesLink.Tests;

public sealed class TilesetParserTests
{
    [Fact]
    public void Parse_GeneratesCompactTileIdsWithoutSlash()
    {
        var parser = new TilesetParser();
        var source = new Uri("https://tile.googleapis.com/v1/3dtiles/root.json?session=s");
        var json = """
                   {
                     "root": {
                       "children": [
                         { "content": { "uri": "0.glb" } },
                         { "content": { "uri": "1.glb" } },
                         { "content": { "uri": "2.glb" } },
                         { "content": { "uri": "3.glb" } },
                         { "content": { "uri": "4.glb" } },
                         { "content": { "uri": "5.glb" } },
                         { "content": { "uri": "6.glb" } },
                         { "content": { "uri": "7.glb" } },
                         { "content": { "uri": "8.glb" } },
                         { "content": { "uri": "9.glb" } },
                         { "content": { "uri": "10.glb" } },
                         { "content": { "uri": "11.glb" } },
                         { "content": { "uri": "12.glb" } },
                         { "content": { "uri": "13.glb" } },
                         { "content": { "uri": "14.glb" } },
                         { "content": { "uri": "15.glb" } },
                         { "content": { "uri": "16.glb" } },
                         { "content": { "uri": "17.glb" } },
                         { "content": { "uri": "18.glb" } },
                         { "content": { "uri": "19.glb" } },
                         { "content": { "uri": "20.glb" } },
                         { "content": { "uri": "21.glb" } },
                         { "content": { "uri": "22.glb" } },
                         { "content": { "uri": "23.glb" } },
                         { "content": { "uri": "24.glb" } },
                         { "content": { "uri": "25.glb" } },
                         { "content": { "uri": "26.glb" } },
                         { "content": { "uri": "27.glb" } },
                         { "content": { "uri": "28.glb" } },
                         { "content": { "uri": "29.glb" } },
                         { "content": { "uri": "30.glb" } },
                         { "content": { "uri": "31.glb" } },
                         { "content": { "uri": "32.glb" } },
                         { "content": { "uri": "33.glb" } },
                         { "content": { "uri": "34.glb" } },
                         { "content": { "uri": "35.glb" } },
                         { "content": { "uri": "36.glb" } }
                       ]
                     }
                   }
                   """;

        var tileset = parser.Parse(json, source);

        tileset.Root.Id.Should().Be("0");
        tileset.Root.Children[0].Id.Should().Be("00");
        tileset.Root.Children[9].Id.Should().Be("09");
        tileset.Root.Children[10].Id.Should().Be("0A");
        tileset.Root.Children[35].Id.Should().Be("0Z");
        tileset.Root.Children[36].Id.Should().Be("0A");
        tileset.Root.Children.Select(c => c.Id).Should().OnlyContain(id => !id.Contains('/'));
    }

    [Fact]
    public void Parse_CollectsCopyrightStrings()
    {
        var parser = new TilesetParser();
        var source = new Uri("https://tile.googleapis.com/v1/3dtiles/root.json?session=s");
        var json = """
                   {
                     "asset": {
                       "version": "1.0",
                       "copyright": "Google, Maxar Technologies"
                     },
                     "root": {
                       "content": { "uri": "0.glb" },
                       "children": [
                         {
                           "content": { "uri": "1.glb" },
                           "copyright": "Google, Airbus"
                         }
                       ]
                     }
                   }
                   """;

        var tileset = parser.Parse(json, source);

        tileset.Copyrights.Should().BeEquivalentTo(
            "Google, Maxar Technologies",
            "Google, Airbus");
    }
}
