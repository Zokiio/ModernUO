namespace Server.Items
{
  public class RuneCarvingKnife : AssassinSpike
  {
    [Constructible]
    public RuneCarvingKnife()
    {
      Hue = 0x48D;

      WeaponAttributes.HitLeechMana = 40;
      Attributes.RegenStam = 2;
      Attributes.LowerManaCost = 10;
      Attributes.WeaponSpeed = 35;
      Attributes.WeaponDamage = 30;
    }

    public RuneCarvingKnife(Serial serial) : base(serial)
    {
    }

    public override int LabelNumber => 1072915; // Rune Carving Knife

    public override void Serialize(IGenericWriter writer)
    {
      base.Serialize(writer);

      writer.WriteEncodedInt(0); // version
    }

    public override void Deserialize(IGenericReader reader)
    {
      base.Deserialize(reader);

      int version = reader.ReadEncodedInt();
    }
  }
}