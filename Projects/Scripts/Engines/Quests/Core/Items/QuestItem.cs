using Server.Mobiles;

namespace Server.Engines.Quests
{
  public abstract class QuestItem : Item
  {
    public QuestItem(int itemID) : base(itemID)
    {
    }

    public QuestItem(Serial serial) : base(serial)
    {
    }

    public virtual bool Accepted => Deleted;

    public abstract bool CanDrop(PlayerMobile pm);

    public override bool DropToWorld(Mobile from, Point3D p)
    {
      bool ret = base.DropToWorld(from, p);

      if (ret && !Accepted && Parent != from.Backpack)
      {
        if (from.AccessLevel > AccessLevel.Player) return true;

        if (!(from is PlayerMobile) || CanDrop((PlayerMobile)from)) return true;
        from.SendLocalizedMessage(
          1049343); // You can only drop quest items into the top-most level of your backpack while you still need them for your quest.
        return false;
      }

      return ret;
    }

    public override bool DropToMobile(Mobile from, Mobile target, Point3D p)
    {
      bool ret = base.DropToMobile(from, target, p);

      if (ret && !Accepted && Parent != from.Backpack)
      {
        if (from.AccessLevel > AccessLevel.Player) return true;

        if (!(from is PlayerMobile) || CanDrop((PlayerMobile)from)) return true;
        from.SendLocalizedMessage(
          1049344); // You decide against trading the item.  You still need it for your quest.
        return false;
      }

      return ret;
    }

    public override bool DropToItem(Mobile from, Item target, Point3D p)
    {
      bool ret = base.DropToItem(from, target, p);

      if (ret && !Accepted && Parent != from.Backpack)
      {
        if (from.AccessLevel > AccessLevel.Player) return true;

        if (!(from is PlayerMobile) || CanDrop((PlayerMobile)from)) return true;
        from.SendLocalizedMessage(
          1049343); // You can only drop quest items into the top-most level of your backpack while you still need them for your quest.
        return false;
      }

      return ret;
    }

    public override DeathMoveResult OnParentDeath(Mobile parent)
    {
      if (parent is PlayerMobile mobile && !CanDrop(mobile))
        return DeathMoveResult.MoveToBackpack;

      return base.OnParentDeath(parent);
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