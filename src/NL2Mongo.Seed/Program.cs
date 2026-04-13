using Bogus;
using Microsoft.Extensions.Logging;
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

var nl2mongoDb       = client.GetDatabase("nl2mongo");
var nl2mongoContacts = nl2mongoDb.GetCollection<Contact>("contacts");

await nl2mongoContacts.DeleteManyAsync(FilterDefinition<Contact>.Empty);
log.LogInformation("Cleared nl2mongo.contacts");

var groups      = new[] { "Newsletter", "VIP", "Trial", "Alumni", "Beta" };
var occupations = new[] { "Engineer", "Teacher", "Nurse", "Retired", "Designer", "Lawyer", "Accountant", "Manager" };

var nl2mongoFaker = new Faker<Contact>()
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

await nl2mongoContacts.InsertManyAsync(nl2mongoFaker.Generate(1000));
log.LogInformation("Inserted 1000 contacts into nl2mongo.contacts");

// ── Tenant 2: dunmowPropertyGroup ─────────────────────────────────────────────

var dunmowDb       = client.GetDatabase("dunmowPropertyGroup");
var dunmowContacts = dunmowDb.GetCollection<Lead>("contacts");

await dunmowContacts.DeleteManyAsync(FilterDefinition<Lead>.Empty);
log.LogInformation("Cleared dunmowPropertyGroup.contacts");

var counties          = new[] { "Essex", "Suffolk", "Norfolk", "Kent", "Surrey", "Hampshire", "Hertfordshire", "Oxfordshire", "Berkshire", "Cambridgeshire" };
var statuses          = new[] { "Lead", "Current Client", "Past Client", "Cold" };
var propertyInterests = new[] { "Buying", "Selling", "Renting", "Letting" };
var sources           = new[] { "Referral", "Website", "Auction", "Cold Call", "Social Media" };

var dunmowFaker = new Faker<Lead>()
    .RuleFor(l => l.FirstName,        f => f.Name.FirstName())
    .RuleFor(l => l.LastName,         f => f.Name.LastName())
    .RuleFor(l => l.Email,            (f, l) => f.Internet.Email(l.FirstName, l.LastName))
    .RuleFor(l => l.Age,              f => f.Random.Int(22, 75))
    .RuleFor(l => l.IsActive,         f => f.Random.Bool(0.65f))
    .RuleFor(l => l.Status,           f => f.PickRandom(statuses))
    .RuleFor(l => l.PropertyInterest, f => f.PickRandom(propertyInterests))
    .RuleFor(l => l.Budget,           f => f.Random.Int(150, 900))
    .RuleFor(l => l.Bedrooms,         f => f.Random.Int(1, 5))
    .RuleFor(l => l.County,           f => f.PickRandom(counties))
    .RuleFor(l => l.Source,           f => f.PickRandom(sources))
    .RuleFor(l => l.CreatedAt,        f => f.Date.Past(2).ToUniversalTime());

await dunmowContacts.InsertManyAsync(dunmowFaker.Generate(1000));
log.LogInformation("Inserted 1000 contacts into dunmowPropertyGroup.contacts");

// ── Records ───────────────────────────────────────────────────────────────────

record Contact
{
    public string   FirstName  { get; init; } = "";
    public string   LastName   { get; init; } = "";
    public string   Email      { get; init; } = "";
    public int      Age        { get; init; }
    public string   Occupation { get; init; } = "";
    public string   City       { get; init; } = "";
    public string   Country    { get; init; } = "";
    public string[] Groups     { get; init; } = [];
    public bool     IsActive   { get; init; }
    public DateTime CreatedAt  { get; init; }
}

record Lead
{
    public string   FirstName        { get; init; } = "";
    public string   LastName         { get; init; } = "";
    public string   Email            { get; init; } = "";
    public int      Age              { get; init; }
    public bool     IsActive         { get; init; }
    public string   Status           { get; init; } = "";
    public string   PropertyInterest { get; init; } = "";
    public int      Budget           { get; init; }
    public int      Bedrooms         { get; init; }
    public string   County           { get; init; } = "";
    public string   Source           { get; init; } = "";
    public DateTime CreatedAt        { get; init; }
}
