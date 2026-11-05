// Generated once (2026-11-05) from EurekaHelper's zone tables; the FATE ids were checked against the
// game's own Fate sheet with Lumina (all 72 are the named FATE). Plain data - edit by hand from here on.
using System.Collections.Generic;

namespace Charon.Features.Eureka;

/// <summary>
/// Every Eureka FATE: the notorious monsters, the bunny FATEs and the two Hydatos specials (Ovni and the
/// Baldesion Arsenal support FATE). FATE id, boss and level are from EurekaHelper (snorux/EurekaHelper,
/// maintained fork KangasZ/EurekaHelper), XIV/Zones/Eureka*.cs - licensed under the Apache License 2.0
/// (http://www.apache.org/licenses/LICENSE-2.0); copied and reformatted, nothing else changed.
/// </summary>
public static class EurekaFateData
{
    public static readonly EurekaFateInfo[] Fates =
    [
        // Anemos
        new(1332, EurekaZones.Anemos, "Sabotender Corrido", 1, EurekaFateKind.NotoriousMonster),
        new(1348, EurekaZones.Anemos, "The Lord of Anemos", 2, EurekaFateKind.NotoriousMonster),
        new(1333, EurekaZones.Anemos, "Teles", 3, EurekaFateKind.NotoriousMonster),
        new(1328, EurekaZones.Anemos, "The Emperor of Anemos", 4, EurekaFateKind.NotoriousMonster),
        new(1344, EurekaZones.Anemos, "Callisto", 5, EurekaFateKind.NotoriousMonster),
        new(1347, EurekaZones.Anemos, "Number", 6, EurekaFateKind.NotoriousMonster),
        new(1345, EurekaZones.Anemos, "Jahannam", 7, EurekaFateKind.NotoriousMonster),
        new(1334, EurekaZones.Anemos, "Amemet", 8, EurekaFateKind.NotoriousMonster),
        new(1335, EurekaZones.Anemos, "Caym", 9, EurekaFateKind.NotoriousMonster),
        new(1336, EurekaZones.Anemos, "Bombadeel", 10, EurekaFateKind.NotoriousMonster),
        new(1339, EurekaZones.Anemos, "Serket", 11, EurekaFateKind.NotoriousMonster),
        new(1346, EurekaZones.Anemos, "Judgemental Julika", 12, EurekaFateKind.NotoriousMonster),
        new(1343, EurekaZones.Anemos, "The White Rider", 13, EurekaFateKind.NotoriousMonster),
        new(1337, EurekaZones.Anemos, "Polyphemus", 14, EurekaFateKind.NotoriousMonster),
        new(1342, EurekaZones.Anemos, "Simurgh's Strider", 15, EurekaFateKind.NotoriousMonster),
        new(1341, EurekaZones.Anemos, "King Hazmat", 16, EurekaFateKind.NotoriousMonster),
        new(1331, EurekaZones.Anemos, "Fafnir", 17, EurekaFateKind.NotoriousMonster),
        new(1340, EurekaZones.Anemos, "Amarok", 18, EurekaFateKind.NotoriousMonster),
        new(1338, EurekaZones.Anemos, "Lamashtu", 19, EurekaFateKind.NotoriousMonster),
        new(1329, EurekaZones.Anemos, "Pazuzu", 20, EurekaFateKind.NotoriousMonster),
        // Pagos
        new(1351, EurekaZones.Pagos, "The Snow Queen", 20, EurekaFateKind.NotoriousMonster),
        new(1369, EurekaZones.Pagos, "Taxim", 21, EurekaFateKind.NotoriousMonster),
        new(1353, EurekaZones.Pagos, "Ash Dragon", 22, EurekaFateKind.NotoriousMonster),
        new(1354, EurekaZones.Pagos, "Glavoid", 23, EurekaFateKind.NotoriousMonster),
        new(1355, EurekaZones.Pagos, "Anapos", 24, EurekaFateKind.NotoriousMonster),
        new(1366, EurekaZones.Pagos, "Hakutaku", 25, EurekaFateKind.NotoriousMonster),
        new(1357, EurekaZones.Pagos, "King Igloo", 26, EurekaFateKind.NotoriousMonster),
        new(1356, EurekaZones.Pagos, "Asag", 27, EurekaFateKind.NotoriousMonster),
        new(1352, EurekaZones.Pagos, "Surabhi", 28, EurekaFateKind.NotoriousMonster),
        new(1360, EurekaZones.Pagos, "King Arthro", 29, EurekaFateKind.NotoriousMonster),
        new(1358, EurekaZones.Pagos, "Mindertaur/Eldertaur", 30, EurekaFateKind.NotoriousMonster),
        new(1361, EurekaZones.Pagos, "Holy Cow", 31, EurekaFateKind.NotoriousMonster),
        new(1362, EurekaZones.Pagos, "Hadhayosh", 32, EurekaFateKind.NotoriousMonster),
        new(1359, EurekaZones.Pagos, "Horus", 33, EurekaFateKind.NotoriousMonster),
        new(1363, EurekaZones.Pagos, "Arch Angra Mainyu", 34, EurekaFateKind.NotoriousMonster),
        new(1365, EurekaZones.Pagos, "Copycat Cassie", 35, EurekaFateKind.NotoriousMonster),
        new(1364, EurekaZones.Pagos, "Louhi", 35, EurekaFateKind.NotoriousMonster),
        new(1367, EurekaZones.Pagos, "", 21, EurekaFateKind.Bunny),
        new(1368, EurekaZones.Pagos, "", 31, EurekaFateKind.Bunny),
        // Pyros
        new(1388, EurekaZones.Pyros, "Leucosia", 35, EurekaFateKind.NotoriousMonster),
        new(1389, EurekaZones.Pyros, "Flauros", 36, EurekaFateKind.NotoriousMonster),
        new(1390, EurekaZones.Pyros, "The Sophist", 37, EurekaFateKind.NotoriousMonster),
        new(1391, EurekaZones.Pyros, "Graffiacane", 38, EurekaFateKind.NotoriousMonster),
        new(1392, EurekaZones.Pyros, "Askalaphos", 39, EurekaFateKind.NotoriousMonster),
        new(1393, EurekaZones.Pyros, "Grand Duke Batym", 40, EurekaFateKind.NotoriousMonster),
        new(1394, EurekaZones.Pyros, "Aetolus", 41, EurekaFateKind.NotoriousMonster),
        new(1395, EurekaZones.Pyros, "Lesath", 42, EurekaFateKind.NotoriousMonster),
        new(1396, EurekaZones.Pyros, "Eldthurs", 43, EurekaFateKind.NotoriousMonster),
        new(1397, EurekaZones.Pyros, "Iris", 44, EurekaFateKind.NotoriousMonster),
        new(1398, EurekaZones.Pyros, "Lamebrix Strikebocks", 45, EurekaFateKind.NotoriousMonster),
        new(1399, EurekaZones.Pyros, "Dux", 46, EurekaFateKind.NotoriousMonster),
        new(1400, EurekaZones.Pyros, "Lumber Jack", 47, EurekaFateKind.NotoriousMonster),
        new(1401, EurekaZones.Pyros, "Glaukopis", 48, EurekaFateKind.NotoriousMonster),
        new(1402, EurekaZones.Pyros, "Ying-Yang", 49, EurekaFateKind.NotoriousMonster),
        new(1403, EurekaZones.Pyros, "Skoll", 50, EurekaFateKind.NotoriousMonster),
        new(1404, EurekaZones.Pyros, "Penthesilea", 50, EurekaFateKind.NotoriousMonster),
        new(1407, EurekaZones.Pyros, "", 35, EurekaFateKind.Bunny),
        new(1408, EurekaZones.Pyros, "", 46, EurekaFateKind.Bunny),
        // Hydatos
        new(1412, EurekaZones.Hydatos, "Khalamari", 50, EurekaFateKind.NotoriousMonster),
        new(1413, EurekaZones.Hydatos, "Stegodon", 51, EurekaFateKind.NotoriousMonster),
        new(1414, EurekaZones.Hydatos, "Molech", 52, EurekaFateKind.NotoriousMonster),
        new(1415, EurekaZones.Hydatos, "Piasa", 53, EurekaFateKind.NotoriousMonster),
        new(1416, EurekaZones.Hydatos, "Frostmane", 54, EurekaFateKind.NotoriousMonster),
        new(1417, EurekaZones.Hydatos, "Daphne", 55, EurekaFateKind.NotoriousMonster),
        new(1418, EurekaZones.Hydatos, "King Goldemar", 56, EurekaFateKind.NotoriousMonster),
        new(1419, EurekaZones.Hydatos, "Leuke", 57, EurekaFateKind.NotoriousMonster),
        new(1420, EurekaZones.Hydatos, "Barong", 58, EurekaFateKind.NotoriousMonster),
        new(1421, EurekaZones.Hydatos, "Ceto", 59, EurekaFateKind.NotoriousMonster),
        new(1423, EurekaZones.Hydatos, "Provenance Watcher", 60, EurekaFateKind.NotoriousMonster),
        new(1424, EurekaZones.Hydatos, "Ovni", 60, EurekaFateKind.Special),
        new(1422, EurekaZones.Hydatos, "Tristitia", 60, EurekaFateKind.Special),
        new(1425, EurekaZones.Hydatos, "", 50, EurekaFateKind.Bunny),
    ];

    /// <summary>
    /// What spawns each notorious monster, from the same EurekaHelper tables (Apache-2.0): the mob whose kills
    /// trigger it (resolved to BNpcName ids with Lumina by English name - all 65 matched; six names carry two
    /// ids, both kept), the weather the NM needs, the weather its trigger mob needs, and whether the trigger
    /// mob only appears at night. 0 = no weather needed. Bunny FATEs, Ovni and the Arsenal FATE have no trigger.
    /// </summary>
    public static readonly EurekaNmTrigger[] Triggers =
    [
        // Anemos
        new(1332, EurekaZones.Anemos, "Flowering Sabotender", [1019, 6816], 0, 0, false),
        new(1348, EurekaZones.Anemos, "Sea Bishop", [6820], 0, 0, false),
        new(1333, EurekaZones.Anemos, "Anemos Harpeia", [6822], 0, 0, false),
        new(1328, EurekaZones.Anemos, "Darner", [6823], 0, 0, false),
        new(1344, EurekaZones.Anemos, "Val Bear", [6826], 0, 0, false),
        new(1347, EurekaZones.Anemos, "Pneumaflayer", [6827], 0, 0, false),
        new(1345, EurekaZones.Anemos, "Typhoon Sprite", [6983, 7771], 0, EurekaWeather.Gales, false),
        new(1334, EurekaZones.Anemos, "Abraxas", [6831], 0, 0, false),
        new(1335, EurekaZones.Anemos, "Stalker Ziz", [6835], 0, 0, false),
        new(1336, EurekaZones.Anemos, "Traveling Gourmand", [6987], 0, 0, true),
        new(1339, EurekaZones.Anemos, "Khor Claw", [6837], 0, 0, false),
        new(1346, EurekaZones.Anemos, "Henbane", [6840, 7835], 0, 0, false),
        new(1343, EurekaZones.Anemos, "Duskfall Dullahan", [7185], 0, 0, true),
        new(1337, EurekaZones.Anemos, "Monoeye", [6843], 0, 0, false),
        new(1342, EurekaZones.Anemos, "Old World Zu", [6845], 0, 0, false),
        new(1341, EurekaZones.Anemos, "Anemos Anala", [6974], 0, 0, false),
        new(1331, EurekaZones.Anemos, "Fossil Dragon", [6990], 0, 0, true),
        new(1340, EurekaZones.Anemos, "Voidscale", [6977], 0, 0, false),
        new(1338, EurekaZones.Anemos, "Val Specter", [6980], 0, 0, true),
        new(1329, EurekaZones.Anemos, "Shadow Wraith", [6989], EurekaWeather.Gales, 0, true),
        // Pagos
        new(1351, EurekaZones.Pagos, "Yukinko", [7413], 0, 0, false),
        new(1369, EurekaZones.Pagos, "Demon of the Incunable", [7465], 0, 0, true),
        new(1353, EurekaZones.Pagos, "Blood Demon", [7434], 0, 0, false),
        new(1354, EurekaZones.Pagos, "Val Worm", [7453], 0, 0, false),
        new(1355, EurekaZones.Pagos, "Snowmelt Sprite", [7471], 0, EurekaWeather.Fog, false),
        new(1366, EurekaZones.Pagos, "Blubber Eyes", [7429], 0, 0, false),
        new(1357, EurekaZones.Pagos, "Huwasi", [7419], 0, 0, false),
        new(1356, EurekaZones.Pagos, "Wandering Opken", [7431], 0, 0, false),
        new(1352, EurekaZones.Pagos, "Pagos Billygoat", [7423], 0, 0, false),
        new(1360, EurekaZones.Pagos, "Val Snipper", [7450], EurekaWeather.Fog, 0, false),
        new(1358, EurekaZones.Pagos, "Lab Minotaur", [7442], 0, 0, false),
        new(1361, EurekaZones.Pagos, "Elder Buffalo", [7447], 0, 0, false),
        new(1362, EurekaZones.Pagos, "Lesser Void Dragon", [7460], EurekaWeather.Thunder, 0, false),
        new(1359, EurekaZones.Pagos, "Void Vouivre", [7445, 8035], EurekaWeather.HeatWaves, 0, false),
        new(1363, EurekaZones.Pagos, "Gawper", [7420], 0, 0, false),
        new(1365, EurekaZones.Pagos, "Ameretat", [7462], EurekaWeather.Blizzards, 0, false),
        new(1364, EurekaZones.Pagos, "Val Corpse", [7468], 0, 0, true),
        // Pyros
        new(1388, EurekaZones.Pyros, "Pyros Bhoot", [7776], 0, 0, true),
        new(1389, EurekaZones.Pyros, "Thunderstorm Sprite", [7470, 7772], 0, EurekaWeather.Thunder, false),
        new(1390, EurekaZones.Pyros, "Pyros Apanda", [7808], 0, 0, false),
        new(1391, EurekaZones.Pyros, "Valking", [7812], 0, 0, false),
        new(1392, EurekaZones.Pyros, "Overdue Tome", [7814], EurekaWeather.UmbralWind, 0, false),
        new(1393, EurekaZones.Pyros, "Dark Troubadour", [7779], 0, 0, true),
        new(1394, EurekaZones.Pyros, "Islandhander", [7821], 0, 0, false),
        new(1395, EurekaZones.Pyros, "Bird Eater", [7824], 0, 0, false),
        new(1396, EurekaZones.Pyros, "Pyros Crab", [7825], 0, 0, false),
        new(1397, EurekaZones.Pyros, "Northern Swallow", [7830], 0, 0, false),
        new(1398, EurekaZones.Pyros, "Illuminati Escapee", [7832], 0, 0, false),
        new(1399, EurekaZones.Pyros, "Matanga Castaway", [7836], EurekaWeather.Thunder, 0, false),
        new(1400, EurekaZones.Pyros, "Pyros Treant", [7837], 0, 0, false),
        new(1401, EurekaZones.Pyros, "Val Skatene", [6841, 7841], 0, 0, false),
        new(1402, EurekaZones.Pyros, "Pyros Hecteyes", [7845], 0, 0, false),
        new(1403, EurekaZones.Pyros, "Pyros Shuck", [7848], EurekaWeather.Blizzards, 0, false),
        new(1404, EurekaZones.Pyros, "Val Bloodglider", [7846], EurekaWeather.HeatWaves, 0, false),
        // Hydatos
        new(1412, EurekaZones.Hydatos, "Xzomit", [8024], 0, 0, false),
        new(1413, EurekaZones.Hydatos, "Hydatos Primelephas", [8028], 0, 0, false),
        new(1414, EurekaZones.Hydatos, "Val Nullchu", [8031], 0, 0, false),
        new(1415, EurekaZones.Hydatos, "Vivid Gastornis", [8032], 0, 0, false),
        new(1416, EurekaZones.Hydatos, "Northern Tiger", [8037], 0, 0, false),
        new(1417, EurekaZones.Hydatos, "Dark Void Monk", [8038], 0, 0, false),
        new(1418, EurekaZones.Hydatos, "Hydatos Wraith", [8058], 0, 0, true),
        new(1419, EurekaZones.Hydatos, "Tigerhawk", [8045], 0, 0, false),
        new(1420, EurekaZones.Hydatos, "Laboratory Lion", [8047], 0, 0, false),
        new(1421, EurekaZones.Hydatos, "Hydatos Delphyne", [8050], 0, 0, false),
        new(1423, EurekaZones.Hydatos, "Crystal Claw", [8054], 0, 0, false),
    ];
}
