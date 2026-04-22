using Bogus;
using Microsoft.Extensions.Logging;
using MongoDB.Bson;
using MongoDB.Bson.Serialization.Attributes;
using MongoDB.Bson.Serialization.Conventions;
using MongoDB.Driver;

using var loggerFactory = LoggerFactory.Create(b => b.AddConsole());
var log = loggerFactory.CreateLogger("Seed");

var pack = new ConventionPack { new CamelCaseElementNameConvention() };
ConventionRegistry.Register("CamelCase", pack, t => true);

// Fixed seed — guarantees identical data on every reseed, required for reliable evaluation tests
Bogus.Randomizer.Seed = new Random(42);

var client = new MongoClient("mongodb://localhost:27017");

// ── Tenant 1: nl2mongo ────────────────────────────────────────────────────────

var nl2mongoDb        = client.GetDatabase("nl2mongo");
var nl2mongoContacts  = nl2mongoDb.GetCollection<Contact>("contacts");
var nl2mongoAddresses = nl2mongoDb.GetCollection<Address>("addresses");

await nl2mongoContacts.DeleteManyAsync(FilterDefinition<Contact>.Empty);
await nl2mongoAddresses.DeleteManyAsync(FilterDefinition<Address>.Empty);
log.LogInformation("Cleared nl2mongo.contacts and nl2mongo.addresses");

var groups      = new[] { "Newsletter", "VIP", "Trial", "Alumni", "Beta" };
var occupations = new[] { "Engineer", "Teacher", "Nurse", "Retired", "Designer", "Lawyer", "Accountant", "Manager" };

var nl2mongoFaker = new Faker<Contact>()
    .RuleFor(c => c.Id,         f => ObjectId.GenerateNewId())
    .RuleFor(c => c.FirstName,  f => f.Name.FirstName())
    .RuleFor(c => c.LastName,   f => f.Name.LastName())
    .RuleFor(c => c.Email,      (f, c) => f.Internet.Email(c.FirstName, c.LastName))
    .RuleFor(c => c.Age,        f => f.Random.Int(18, 80))
    .RuleFor(c => c.Occupation, f => f.PickRandom(occupations))
    .RuleFor(c => c.City,       f => f.Address.City())
    .RuleFor(c => c.Country,    f => f.Address.Country())
    .RuleFor(c => c.Groups,     f => f.PickRandom(groups, f.Random.Int(1, 3)).Distinct().ToArray())
    .RuleFor(c => c.IsActive,   f => f.Random.Bool(0.75f))
    .RuleFor(c => c.CreatedAt,  f => f.Date.Past(3).ToUniversalTime());

var contacts = nl2mongoFaker.Generate(1000);
await nl2mongoContacts.InsertManyAsync(contacts);
log.LogInformation("Inserted 1000 contacts into nl2mongo.contacts");

// ~80% of contacts get an address so there are some without (tests preserveNullAndEmptyArrays)
var addressFaker = new Faker<Address>()
    .RuleFor(a => a.Id,       f => ObjectId.GenerateNewId())
    .RuleFor(a => a.Street,   f => f.Address.StreetAddress())
    .RuleFor(a => a.City,     f => f.Address.City())
    .RuleFor(a => a.Postcode, f => f.Address.ZipCode())
    .RuleFor(a => a.Country,  f => f.Address.Country());

var rng       = new Random(42);
var addresses = contacts
    .Where(_ => rng.NextDouble() < 0.8)
    .Select(c => addressFaker.Generate() with { ContactId = c.Id })
    .ToList();

await nl2mongoAddresses.InsertManyAsync(addresses);
log.LogInformation("Inserted {Count} addresses into nl2mongo.addresses", addresses.Count);

// ── Tenant 2: dunmowPropertyGroup ─────────────────────────────────────────────

var dunmowDb           = client.GetDatabase("dunmowPropertyGroup");
var dunmowContacts     = dunmowDb.GetCollection<Lead>("contacts");
var dunmowProperties   = dunmowDb.GetCollection<Property>("properties");
var dunmowRequirements = dunmowDb.GetCollection<Requirement>("requirements");

await dunmowContacts.DeleteManyAsync(FilterDefinition<Lead>.Empty);
await dunmowProperties.DeleteManyAsync(FilterDefinition<Property>.Empty);
await dunmowRequirements.DeleteManyAsync(FilterDefinition<Requirement>.Empty);
log.LogInformation("Cleared dunmowPropertyGroup.contacts, properties, and requirements");

var counties          = new[] { "Essex", "Suffolk", "Norfolk", "Kent", "Surrey", "Hampshire", "Hertfordshire", "Oxfordshire", "Berkshire", "Cambridgeshire" };
var statuses          = new[] { "Lead", "Current Client", "Past Client", "Cold" };
var sources           = new[] { "Referral", "Website", "Auction", "Cold Call", "Social Media" };
var propertyTypes     = new[] { "Detached", "Semi-Detached", "Terraced", "Flat", "Bungalow" };

// propertyInterest drives role:
//   Buying / Renting  → Applicant (they want a property)
//   Selling / Letting → Landlord  (they have a property)
var applicantInterests = new[] { "Buying", "Renting" };
var landlordInterests  = new[] { "Selling", "Letting" };

var dunmowFaker = new Faker<Lead>()
    .RuleFor(l => l.Id,               f => ObjectId.GenerateNewId())
    .RuleFor(l => l.FirstName,        f => f.Name.FirstName())
    .RuleFor(l => l.LastName,         f => f.Name.LastName())
    .RuleFor(l => l.Email,            (f, l) => f.Internet.Email(l.FirstName, l.LastName))
    .RuleFor(l => l.Age,              f => f.Random.Int(22, 75))
    .RuleFor(l => l.IsActive,         f => f.Random.Bool(0.65f))
    .RuleFor(l => l.Status,           f => f.PickRandom(statuses))
    .RuleFor(l => l.PropertyInterest, f => f.PickRandom(applicantInterests.Concat(landlordInterests).ToArray()))
    .RuleFor(l => l.Role,             (f, l) => applicantInterests.Contains(l.PropertyInterest) ? "Applicant" : "Landlord")
    .RuleFor(l => l.Budget,           f => f.Random.Int(150, 900))
    .RuleFor(l => l.Bedrooms,         f => f.Random.Int(1, 5))
    .RuleFor(l => l.County,           f => f.PickRandom(counties))
    .RuleFor(l => l.Source,           f => f.PickRandom(sources))
    .RuleFor(l => l.CreatedAt,        f => f.Date.Past(2).ToUniversalTime());

var leads = dunmowFaker.Generate(1000);
await dunmowContacts.InsertManyAsync(leads);
log.LogInformation("Inserted 1000 contacts into dunmowPropertyGroup.contacts");

var landlords  = leads.Where(l => l.Role == "Landlord").ToList();
var applicants = leads.Where(l => l.Role == "Applicant").ToList();
log.LogInformation("  {Landlords} landlords, {Applicants} applicants", landlords.Count, applicants.Count);

// Each landlord gets 1–2 properties
var propertyFaker = new Faker<Property>()
    .RuleFor(p => p.Id,           f => ObjectId.GenerateNewId())
    .RuleFor(p => p.Street,       f => f.Address.StreetAddress())
    .RuleFor(p => p.Town,         f => f.Address.City())
    .RuleFor(p => p.Postcode,     f => f.Address.ZipCode())
    .RuleFor(p => p.PropertyType, f => f.PickRandom(propertyTypes))
    .RuleFor(p => p.Bedrooms,     f => f.Random.Int(1, 6))
    .RuleFor(p => p.AskingPrice,  f => f.Random.Int(150, 1500));

var rng2       = new Random(42);
var properties = landlords.SelectMany(l =>
{
    var count = rng2.NextDouble() < 0.35 ? 2 : 1;
    return Enumerable.Range(0, count).Select(_ =>
        propertyFaker.Generate() with { LandlordId = l.Id, County = l.County });
}).ToList();

await dunmowProperties.InsertManyAsync(properties);
log.LogInformation("Inserted {Count} properties into dunmowPropertyGroup.properties", properties.Count);

// ~85% of applicants have a requirements record; the rest haven't submitted preferences yet
var requirementFaker = new Faker<Requirement>()
    .RuleFor(r => r.Id,                 f => ObjectId.GenerateNewId())
    .RuleFor(r => r.PropertyType,       f => f.PickRandom(propertyTypes))
    .RuleFor(r => r.MinBedrooms,        f => f.Random.Int(1, 3))
    .RuleFor(r => r.MaxBedrooms,        (f, r) => f.Random.Int(r.MinBedrooms, r.MinBedrooms + 2))
    .RuleFor(r => r.MinBudget,          f => f.Random.Int(100, 400))
    .RuleFor(r => r.MaxBudget,          (f, r) => f.Random.Int(r.MinBudget + 50, r.MinBudget + 500))
    .RuleFor(r => r.PreferredCounties,  f => f.PickRandom(counties, f.Random.Int(1, 3)).Distinct().ToArray())
    .RuleFor(r => r.MoveByDate,         f => f.Date.Future(2).ToUniversalTime())
    .RuleFor(r => r.IsActive,           f => f.Random.Bool(0.8f));

var rng3         = new Random(42);
var requirements = applicants
    .Where(_ => rng3.NextDouble() < 0.85)
    .Select(a => requirementFaker.Generate() with { ApplicantId = a.Id })
    .ToList();

await dunmowRequirements.InsertManyAsync(requirements);
log.LogInformation("Inserted {Count} requirements into dunmowPropertyGroup.requirements", requirements.Count);

// ── Records ───────────────────────────────────────────────────────────────────

record Contact
{
    [BsonId] public ObjectId Id        { get; init; }
    public string   FirstName          { get; init; } = "";
    public string   LastName           { get; init; } = "";
    public string   Email              { get; init; } = "";
    public int      Age                { get; init; }
    public string   Occupation         { get; init; } = "";
    public string   City               { get; init; } = "";
    public string   Country            { get; init; } = "";
    public string[] Groups             { get; init; } = [];
    public bool     IsActive           { get; init; }
    public DateTime CreatedAt          { get; init; }
}

record Address
{
    [BsonId] public ObjectId Id        { get; init; }
    public ObjectId ContactId          { get; init; }
    public string   Street             { get; init; } = "";
    public string   City               { get; init; } = "";
    public string   Postcode           { get; init; } = "";
    public string   Country            { get; init; } = "";
}

record Lead
{
    [BsonId] public ObjectId Id        { get; init; }
    public string   FirstName          { get; init; } = "";
    public string   LastName           { get; init; } = "";
    public string   Email              { get; init; } = "";
    public int      Age                { get; init; }
    public bool     IsActive           { get; init; }
    public string   Status             { get; init; } = "";
    public string   PropertyInterest   { get; init; } = "";
    // "Applicant" (Buying/Renting) or "Landlord" (Selling/Letting)
    public string   Role               { get; init; } = "";
    public int      Budget             { get; init; }
    public int      Bedrooms           { get; init; }
    public string   County             { get; init; } = "";
    public string   Source             { get; init; } = "";
    public DateTime CreatedAt          { get; init; }
}

record Property
{
    [BsonId] public ObjectId Id        { get; init; }
    public ObjectId LandlordId         { get; init; }
    public string   Street             { get; init; } = "";
    public string   Town               { get; init; } = "";
    public string   County             { get; init; } = "";
    public string   Postcode           { get; init; } = "";
    public string   PropertyType       { get; init; } = "";
    public int      Bedrooms           { get; init; }
    public int      AskingPrice        { get; init; }
}

record Requirement
{
    [BsonId] public ObjectId Id              { get; init; }
    public ObjectId   ApplicantId            { get; init; }
    public string     PropertyType           { get; init; } = "";
    public int        MinBedrooms            { get; init; }
    public int        MaxBedrooms            { get; init; }
    public int        MinBudget              { get; init; }
    public int        MaxBudget              { get; init; }
    public string[]   PreferredCounties      { get; init; } = [];
    public DateTime   MoveByDate             { get; init; }
    public bool       IsActive               { get; init; }
}
