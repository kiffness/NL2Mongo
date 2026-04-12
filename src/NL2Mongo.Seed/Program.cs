using Bogus;
using MongoDB.Driver;

const string connectionString = "mongodb://localhost:27017";
const string databaseName = "nl2mongo";
const string collectionName = "contacts";
const int contactCount = 500;

var client = new MongoClient(connectionString);
var db = client.GetDatabase(databaseName);
var collection = db.GetCollection<Contact>(collectionName);

// Clear existing data so reruns are safe
await collection.DeleteManyAsync(FilterDefinition<Contact>.Empty);
Console.WriteLine("Cleared existing contacts.");

var groups = new[] { "Newsletter", "VIP", "Trial", "Alumni", "Beta" };
var occupations = new[] { "Engineer", "Teacher", "Nurse", "Retired", "Designer", "Lawyer", "Accountant", "Manager" };

var faker = new Faker<Contact>()
    .RuleFor(c => c.FirstName, f => f.Name.FirstName())
    .RuleFor(c => c.LastName, f => f.Name.LastName())
    .RuleFor(c => c.Email, (f, c) => f.Internet.Email(c.FirstName, c.LastName))
    .RuleFor(c => c.Age, f => f.Random.Int(18, 80))
    .RuleFor(c => c.Occupation, f => f.PickRandom(occupations))
    .RuleFor(c => c.City, f => f.Address.City())
    .RuleFor(c => c.Country, f => f.Address.Country())
    .RuleFor(c => c.Groups, f => f.PickRandom(groups, f.Random.Int(1, 3)).Distinct().ToArray())
    .RuleFor(c => c.IsActive, f => f.Random.Bool(0.75f))
    .RuleFor(c => c.CreatedAt, f => f.Date.Past(3).ToUniversalTime());

var contacts = faker.Generate(contactCount);
await collection.InsertManyAsync(contacts);

Console.WriteLine($"Inserted {contactCount} contacts into '{databaseName}.{collectionName}'.");

record Contact
{
    public string FirstName { get; init; } = "";
    public string LastName { get; init; } = "";
    public string Email { get; init; } = "";
    public int Age { get; init; }
    public string Occupation { get; init; } = "";
    public string City { get; init; } = "";
    public string Country { get; init; } = "";
    public string[] Groups { get; init; } = [];
    public bool IsActive { get; init; }
    public DateTime CreatedAt { get; init; }
}
