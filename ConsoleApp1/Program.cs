string roomName = "Room A";
double area = 35;
if (area<10)
{
    Console.WriteLine($"{roomName} is a Store ({area} m²)");
}
else if (area<30)
{
    Console.WriteLine($"{roomName} is an office ({area} m²)");
}
else
{
    Console.WriteLine($"{roomName} is a Meeting room ({area} m²)");
}