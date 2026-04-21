using FluentAssertions;
using ThreeDTilesLink.Core.Models;
using ThreeDTilesLink.Core.Tiles;

namespace ThreeDTilesLink.Tests
{
    public sealed class TilesetParserTests
    {
        [Fact]
        public void Parse_GeneratesCompactHexTileDisplayLabelsWithoutSlash()
        {
            var sourceUri = new Uri("https://tile.googleapis.com/v1/3dtiles/root.json?session=s");
            TileSourceOptions source = new(
                sourceUri,
                new TileSourceAccess("key", null),
                TileSourceContentLinkOptions.CreateGoogleDefaults());
            string json = /*lang=json,strict*/ """
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
                         { "content": { "uri": "15.glb" } }
                       ]
                     }
                   }
                   """;

            Tileset tileset = new TilesetParser().Parse(json, source.ContentLinks, sourceUri);

            _ = tileset.Root.Id.Should().Be("0");
            _ = tileset.Root.Children[0].Id.Should().Be("00");
            _ = tileset.Root.Children[9].Id.Should().Be("09");
            _ = tileset.Root.Children[10].Id.Should().Be("0A");
            _ = tileset.Root.Children[15].Id.Should().Be("0F");
            _ = tileset.Root.Children.Select(c => c.Id).Should().OnlyContain(id => !id.Contains('/'));
        }

        [Fact]
        public void Parse_WrapsDisplayLabelsAfterHexRangeButKeepsStablePathsUnique()
        {
            var sourceUri = new Uri("https://tile.googleapis.com/v1/3dtiles/root.json?session=s");
            TileSourceOptions source = new(
                sourceUri,
                new TileSourceAccess("key", null),
                TileSourceContentLinkOptions.CreateGoogleDefaults());
            string json = /*lang=json,strict*/ """
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

            Tileset tileset = new TilesetParser().Parse(json, source.ContentLinks, sourceUri);

            _ = tileset.Root.Children[15].Id.Should().Be("0F");
            _ = tileset.Root.Children[16].Id.Should().Be("00");
            _ = tileset.Root.Children[16].StablePath.Should().Be("0/16");
            _ = tileset.Root.Children[35].Id.Should().Be("03");
            _ = tileset.Root.Children[36].Id.Should().Be("04");
            _ = tileset.Root.Children.Select(child => child.StablePath).Should().OnlyHaveUniqueItems();
        }

        [Fact]
        public void Parse_PreservesConfiguredFileSchemeBasePathAndInheritedQueryParameters()
        {
            var sourceUri = new Uri("https://plateau.example.com/tiles/root.json?sig=abc&unused=z");
            TileSourceOptions source = new(
                sourceUri,
                new TileSourceAccess(null, "token"),
                new TileSourceContentLinkOptions(
                    new Uri("https://cdn.plateau.example.com/tiles/"),
                    ["sig"]));
            string json = """
                {
                  "root": {
                    "content": {
                      "uri": "file:///lod/leaf.glb"
                    }
                  }
                }
                """;

            Tileset tileset = new TilesetParser().Parse(json, source.ContentLinks, sourceUri);

            _ = tileset.Root.ContentUri!.AbsoluteUri.Should().Be("https://cdn.plateau.example.com/tiles/lod/leaf.glb?sig=abc");
        }
    }
}
