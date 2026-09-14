namespace AiCad.Model
{
    /// <summary>
    /// The plan AICADTEST draws. It exercises a generator, a primitive and a
    /// nested repeat, and doubles as the worked example of the output format.
    /// Kept free of AutoCAD types so it can be parsed in a headless test.
    /// </summary>
    public static class SamplePlan
    {
        public const string Json = @"{
  ""name"": ""Sample enclosure 800 x 600 x 250"",
  ""notes"": ""Offline sample: no API call was made."",
  ""layers"": [
    { ""name"": ""AI-OUTLINE"", ""color"": 7,  ""lineWeight"": 0.35 },
    { ""name"": ""AI-PLATE"",   ""color"": 3 },
    { ""name"": ""AI-RAIL"",    ""color"": 5 },
    { ""name"": ""AI-CENTRE"",  ""color"": 1 },
    { ""name"": ""AI-DIM"",     ""color"": 4 },
    { ""name"": ""AI-TEXT"",    ""color"": 2 }
  ],
  ""ops"": [
    { ""op"": ""enclosure"", ""origin"": [0, 0], ""width"": 800, ""height"": 600, ""depth"": 250,
      ""plateInset"": 25, ""glandPlateHeight"": 60, ""railCount"": 3, ""door"": true,
      ""dimensions"": true, ""sideView"": true, ""label"": ""SAMPLE PANEL 800 x 600 x 250"" },
    { ""op"": ""boltpattern"", ""mode"": ""rectangular"", ""center"": [400, 300],
      ""rows"": 2, ""cols"": 2, ""spacingX"": 700, ""spacingY"": 500, ""holeDiameter"": 9 },
    { ""op"": ""titleblock"", ""origin"": [-40, -220], ""sheetWidth"": 1200, ""sheetHeight"": 900,
      ""title"": ""SAMPLE PANEL"", ""drawing"": ""AI-0001"", ""drawnBy"": ""AiCad"", ""scale"": ""1:5"" }
  ]
}";
    }
}
