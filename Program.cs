// Program.cs — Skurk AB API
// ASP.NET Core Minimal API: endpoints definieras direkt här, inga controllers.
//
// Starta lokalt:  dotnet run
// Swagger UI:     https://localhost:{port}/swagger
//
// Azure-konfiguration (görs i portalen, inte i koden):
// 1. CORS: App Service → API → CORS → lägg till din frontend-URL
// 2. Easy Auth: App Service → Authentication → Add identity provider → Microsoft
//    Välj din Entra ID-tenant. Alla anrop kräver nu inloggning.
// 3. Roller för denna skoluppgift hanteras i applikationen nedan, eftersom
//    Easy Auth identifierar användaren men Entra-roller inte kan tilldelas av gruppen.

using System.Text;
using System.Text.Json;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();

// CORS — hanteras primärt i Azure Portal: App Service → API → CORS
// Lägg till din frontend-URL där, så slipper du ändra och redeploya koden.
// Den här koden hanterar CORS lokalt under utveckling.
builder.Services.AddCors(options =>
{
    options.AddPolicy("SkurkPolicy", policy =>
    {
        var origins = builder.Configuration
                             .GetSection("AllowedOrigins")
                             .Get<string[]>() ?? [];
        policy.WithOrigins(origins)
              .AllowAnyHeader()
              .AllowAnyMethod();
    });
});

var app = builder.Build();

app.UseSwagger();
app.UseSwaggerUI();
app.UseCors("SkurkPolicy");

// -------------------------------------------------------
// In-memory datastore med seed-data
// Datan nollställs vid omstart — en riktig app använder databas
// -------------------------------------------------------

var uppdrag = new List<Uppdrag> { new(1, "Operation Mörkblå", "Pågående", "KRITISK", "Ronny Rövare") };
var konsulter = new List<Konsult> { new(1, "Ronny Rövare", "070-666666", "Mörkret 1", "Stockholm") };
var nastaUppdragId = 2;
var nastaKonsultId = 2;

// ======================================================
// Uppdrag
// ======================================================

// Alla roller får läsa uppdrag
app.MapGet("/uppdrag", () => uppdrag)
   .WithName("HamtaUppdrag")
   .WithSummary("Hämta alla uppdrag — alla roller");

// Konsultchef och Admin får skapa uppdrag
app.MapPost("/uppdrag", (NyttUppdrag nytt, HttpRequest req) =>
{
    if (!HarBehorighet(HamtaRoll(req), "Konsultchef")) return Results.StatusCode(403);
    var u = new Uppdrag(nastaUppdragId++, nytt.Titel, "Planeras", nytt.Prioritet ?? "Medel", "");
    uppdrag.Add(u);
    return Results.Created($"/uppdrag/{u.Id}", u);
})
.WithName("SkapaUppdrag")
.WithSummary("Skapa uppdrag — kräver Konsultchef eller Admin");

// Mellanchef och högre får uppdatera status och prioritet
app.MapPut("/uppdrag/{id}", (int id, UppdragUpdate update, HttpRequest req) =>
{
    if (!HarBehorighet(HamtaRoll(req), "Mellanchef")) return Results.StatusCode(403);
    var index = uppdrag.FindIndex(u => u.Id == id);
    if (index < 0) return Results.NotFound();
    uppdrag[index] = uppdrag[index] with
    {
        Status = update.Status ?? uppdrag[index].Status,
        Prioritet = update.Prioritet ?? uppdrag[index].Prioritet
    };
    return Results.Ok(uppdrag[index]);
})
.WithName("UppdateraUppdrag")
.WithSummary("Uppdatera uppdrag — kräver Mellanchef eller högre");

// Bara Admin får ta bort uppdrag — testa med Postman som Praktikant för att se 403
app.MapDelete("/uppdrag/{id}", (int id, HttpRequest req) =>
{
    if (!HarBehorighet(HamtaRoll(req), "Admin")) return Results.StatusCode(403);
    var u = uppdrag.FirstOrDefault(u => u.Id == id);
    if (u is null) return Results.NotFound();
    uppdrag.Remove(u);
    return Results.NoContent();
})
.WithName("AvbrytUppdrag")
.WithSummary("Ta bort uppdrag — kräver Admin");

// Konsultchef och Admin får tilldela konsulter
app.MapPut("/uppdrag/{id}/konsult", (int id, KonsultTilldelning tilldelning, HttpRequest req) =>
{
    if (!HarBehorighet(HamtaRoll(req), "Konsultchef")) return Results.StatusCode(403);
    var index = uppdrag.FindIndex(u => u.Id == id);
    if (index < 0) return Results.NotFound();
    uppdrag[index] = uppdrag[index] with { Konsult = tilldelning.KonsultNamn };
    return Results.Ok(uppdrag[index]);
})
.WithName("TilldelaKonsult")
.WithSummary("Tilldela konsult till uppdrag — kräver Konsultchef eller Admin");

// ======================================================
// Konsulter
// ======================================================

// Alla roller får läsa — men vad som syns beror på rollen
app.MapGet("/konsulter", (HttpRequest req) =>
{
    var roll = HamtaRoll(req);

    // Filtrera fält baserat på roll — det är detta RBAC innebär på datanivå
    return konsulter.Select(k => roll switch
    {
        "Praktikant" => k with { Adress = null, Stad = null },  // ser bara namn + telefon
        "Mellanchef" => k with { Telefon = null },              // ser namn + adress/stad
        _ => k                                        // Konsultchef och Admin ser allt
    });
})
.WithName("HamtaKonsulter")
.WithSummary("Hämta konsulter — fält filtreras baserat på din roll");

// Konsultchef och Admin får registrera konsulter
app.MapPost("/konsulter", (NyKonsult ny, HttpRequest req) =>
{
    if (!HarBehorighet(HamtaRoll(req), "Konsultchef")) return Results.StatusCode(403);
    var k = new Konsult(nastaKonsultId++, ny.Namn, ny.Telefon, ny.Adress, ny.Stad);
    konsulter.Add(k);
    return Results.Created($"/konsulter/{k.Id}", k);
})
.WithName("SkapaKonsult")
.WithSummary("Registrera konsult — kräver Konsultchef eller Admin");

// Mellanchef och högre får uppdatera konsultuppgifter
app.MapPut("/konsulter/{id}", (int id, KonsultUpdate update, HttpRequest req) =>
{
    if (!HarBehorighet(HamtaRoll(req), "Mellanchef")) return Results.StatusCode(403);
    var index = konsulter.FindIndex(k => k.Id == id);
    if (index < 0) return Results.NotFound();
    konsulter[index] = konsulter[index] with
    {
        Namn = update.Namn ?? konsulter[index].Namn,
        Telefon = update.Telefon ?? konsulter[index].Telefon,
        Adress = update.Adress ?? konsulter[index].Adress,
        Stad = update.Stad ?? konsulter[index].Stad
    };
    return Results.Ok(konsulter[index]);
})
.WithName("UppdateraKonsult")
.WithSummary("Uppdatera konsult — kräver Mellanchef eller högre");

// Bara Admin får radera konsulter
app.MapDelete("/konsulter/{id}", (int id, HttpRequest req) =>
{
    if (!HarBehorighet(HamtaRoll(req), "Admin")) return Results.StatusCode(403);
    var k = konsulter.FirstOrDefault(k => k.Id == id);
    if (k is null) return Results.NotFound();
    konsulter.Remove(k);
    return Results.NoContent();
})
.WithName("RaderaKonsult")
.WithSummary("Radera konsult — kräver Admin");

app.Run();

// ======================================================
// Rollkontroll
// ======================================================

// Easy Auth verifierar först Microsoft-inloggningen och skickar sedan användarens
// claims i X-MS-CLIENT-PRINCIPAL. Vi läser e-post/UPN därifrån och mappar den till
// en applikationsroll. Det kräver inga Entra App Roles eller Entra-administratör.
// Lokalt, utan Easy Auth-header, används Admin så att Swagger fungerar som tidigare.
string HamtaRoll(HttpRequest request)
{
    var principalHeader = request.Headers["X-MS-CLIENT-PRINCIPAL"].FirstOrDefault();
    if (string.IsNullOrWhiteSpace(principalHeader)) return "Admin"; // lokal utveckling

    var epost = HamtaEasyAuthEpost(principalHeader)
                ?? request.Headers["X-MS-CLIENT-PRINCIPAL-NAME"].FirstOrDefault();

    return epost?.Trim().ToLowerInvariant() switch
    {
        "jack.reveny@ithogskolan.onmicrosoft.com" => "Admin",
        "christian.savic@ithogskolan.onmicrosoft.com" => "Mellanchef",
        "hilal.ozkan@ithogskolan.onmicrosoft.com" => "Konsultchef",
        _ => "Praktikant"
    };
}

// Olika Microsoft-konfigurationer kan lämna e-postadressen i olika claim-typer.
// Därför stödjer vi både standardiserade och Microsoft-specifika namn.
string? HamtaEasyAuthEpost(string principalHeader)
{
    try
    {
        var json = Encoding.UTF8.GetString(Convert.FromBase64String(principalHeader));
        using var doc = JsonDocument.Parse(json);

        foreach (var claim in doc.RootElement.GetProperty("claims").EnumerateArray())
        {
            var typ = claim.GetProperty("typ").GetString();
            if (typ is "preferred_username" or "email" or "emails" or "upn" or
                "http://schemas.xmlsoap.org/ws/2005/05/identity/claims/emailaddress" or
                "http://schemas.xmlsoap.org/ws/2005/05/identity/claims/upn")
            {
                return claim.GetProperty("val").GetString();
            }
        }
    }
    catch (FormatException) { }
    catch (JsonException) { }
    catch (KeyNotFoundException) { }

    return null;
}

// Kontrollerar om en roll har tillräcklig behörighet.
// Hierarki: Praktikant < Mellanchef < Konsultchef < Admin
bool HarBehorighet(string roll, string kravRoll) => (roll, kravRoll) switch
{
    (_, "Praktikant") => true,
    ("Mellanchef" or "Konsultchef" or "Admin", "Mellanchef") => true,
    ("Konsultchef" or "Admin", "Konsultchef") => true,
    ("Admin", "Admin") => true,
    _ => false
};

// ======================================================
// Datamodeller
// ======================================================

record Uppdrag(int Id, string Titel, string Status, string Prioritet, string Konsult);
record Konsult(int Id, string Namn, string? Telefon, string? Adress, string? Stad);

record NyttUppdrag(string Titel, string? Prioritet);
record UppdragUpdate(string? Status, string? Prioritet);
record KonsultTilldelning(string KonsultNamn);
record NyKonsult(string Namn, string? Telefon, string? Adress, string? Stad);
record KonsultUpdate(string? Namn, string? Telefon, string? Adress, string? Stad);
