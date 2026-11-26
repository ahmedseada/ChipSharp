using System;

// A vending machine, the kind of state machine firmware is made of: an enum for its state and one for its products,
// a struct for each slot, an array of the slots, and what it has taken in static fields. Each call is one event.
class Program
{
    static State state = State.Idle;
    static int credit;
    static int sold;
    static long earned;

    static void Main()
    {
        Slot[] slots = new Slot[3];
        slots[(int)Product.Water] = new Slot(100, 2);
        slots[(int)Product.Juice] = new Slot(150, 1);
        slots[(int)Product.Snack] = new Slot(120, 0);

        Insert(50);
        Choose(slots, Product.Water);   // -1: 50 is not enough
        Insert(50);
        Choose(slots, Product.Water);   // 0: a water, no change
        Insert(200);
        Choose(slots, Product.Juice);   // 50: a juice, and 50 back
        Insert(100);
        Choose(slots, Product.Snack);   // -1: no snacks left, the credit stays
        Choose(slots, Product.Water);   // 0: the last water
        Insert(100);                    // 100: sold out, the coin comes back

        Console.WriteLine(sold);
        Console.WriteLine(earned);
        Console.WriteLine(Stock(slots));
        Console.WriteLine(state == State.SoldOut);
    }

    static void Insert(int cents)
    {
        if (state == State.SoldOut)
        {
            Console.WriteLine(cents);
            return;
        }

        credit += cents;
        state = State.HasCredit;
    }

    // Prints the change, or -1 when nothing comes out.
    static void Choose(Slot[] slots, Product product)
    {
        ref Slot slot = ref slots[(int)product];
        if (state != State.HasCredit || slot.Count == 0 || credit < slot.Price)
        {
            Console.WriteLine(-1);
            return;
        }

        slot.Count--;
        sold++;
        earned += slot.Price;
        Console.WriteLine(credit - slot.Price);
        credit = 0;
        state = Stock(slots) == 0 ? State.SoldOut : State.Idle;
    }

    static int Stock(Slot[] slots)
    {
        int total = 0;
        foreach (Slot slot in slots)
        {
            total += slot.Count;
        }

        return total;
    }
}

enum State
{
    Idle,
    HasCredit,
    SoldOut,
}

enum Product
{
    Water,
    Juice,
    Snack,
}

struct Slot(int price, int count)
{
    public int Price = price;   // in cents
    public int Count = count;
}
