namespace Server.Items
{
  public class MagicTrapScroll : SpellScroll
  {
    [Constructible]
    public MagicTrapScroll(int amount = 1) : base(12, 0x1F39, amount)
    {
    }

    public MagicTrapScroll(Serial serial) : base(serial)
    {
    }

    public override void Serialize(IGenericWriter writer)
    {
      base.Serialize(writer);

      writer.Write(0); // version
    }

    public override void Deserialize(IGenericReader reader)
    {
      base.Deserialize(reader);

      int version = reader.ReadInt();
    }
  }
}
