/***************************************************************************
 *                                  Item.cs
 *                            -------------------
 *   begin                : May 1, 2002
 *   copyright            : (C) The RunUO Software Team
 *   email                : info@runuo.com
 *
 *   $Id$
 *
 ***************************************************************************/

/***************************************************************************
 *
 *   This program is free software; you can redistribute it and/or modify
 *   it under the terms of the GNU General Public License as published by
 *   the Free Software Foundation; either version 2 of the License, or
 *   (at your option) any later version.
 *
 ***************************************************************************/

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Server.ContextMenus;
using Server.Items;
using Server.Network;
using Server.Targeting;

namespace Server
{
  /// <summary>
  ///   Internal flags used to signal how the item should be updated and resent to nearby clients.
  /// </summary>
  [Flags]
  public enum ItemDelta
  {
    /// <summary>
    ///   Nothing.
    /// </summary>
    None = 0x00000000,

    /// <summary>
    ///   Resend the item.
    /// </summary>
    Update = 0x00000001,

    /// <summary>
    ///   Resend the item only if it is equipped.
    /// </summary>
    EquipOnly = 0x00000002,

    /// <summary>
    ///   Resend the item's properties.
    /// </summary>
    Properties = 0x00000004
  }

  /// <summary>
  ///   Enumeration containing possible ways to handle item ownership on death.
  /// </summary>
  public enum DeathMoveResult
  {
    /// <summary>
    ///   The item should be placed onto the corpse.
    /// </summary>
    MoveToCorpse,

    /// <summary>
    ///   The item should remain equipped.
    /// </summary>
    RemainEquipped,

    /// <summary>
    ///   The item should be placed into the owners backpack.
    /// </summary>
    MoveToBackpack
  }

  /// <summary>
  ///   Enumeration of an item's loot and steal state.
  /// </summary>
  public enum LootType : byte
  {
    /// <summary>
    ///   Stealable. Lootable.
    /// </summary>
    Regular = 0,

    /// <summary>
    ///   Unstealable. Unlootable, unless owned by a murderer.
    /// </summary>
    Newbied = 1,

    /// <summary>
    ///   Unstealable. Unlootable, always.
    /// </summary>
    Blessed = 2,

    /// <summary>
    ///   Stealable. Lootable, always.
    /// </summary>
    Cursed = 3
  }

  public class BounceInfo
  {
    public Point3D m_Location, m_WorldLoc;
    public Map m_Map;
    public IEntity m_Parent;

    public BounceInfo(Item item)
    {
      m_Map = item.Map;
      m_Location = item.Location;
      m_WorldLoc = item.GetWorldLocation();
      m_Parent = item.Parent;
    }

    private BounceInfo(Map map, Point3D loc, Point3D worldLoc, IEntity parent)
    {
      m_Map = map;
      m_Location = loc;
      m_WorldLoc = worldLoc;
      m_Parent = parent;
    }

    public static BounceInfo Deserialize(IGenericReader reader)
    {
      if (reader.ReadBool())
      {
        Map map = reader.ReadMap();
        Point3D loc = reader.ReadPoint3D();
        Point3D worldLoc = reader.ReadPoint3D();

        IEntity parent;

        Serial serial = reader.ReadUInt();

        if (serial.IsItem)
          parent = World.FindItem(serial);
        else if (serial.IsMobile)
          parent = World.FindMobile(serial);
        else
          parent = null;

        return new BounceInfo(map, loc, worldLoc, parent);
      }

      return null;
    }

    public static void Serialize(BounceInfo info, IGenericWriter writer)
    {
      if (info == null)
      {
        writer.Write(false);
      }
      else
      {
        writer.Write(true);

        writer.Write(info.m_Map);
        writer.Write(info.m_Location);
        writer.Write(info.m_WorldLoc);

        if (info.m_Parent is Mobile mobile)
          writer.Write(mobile);
        else if (info.m_Parent is Item item)
          writer.Write(item);
        else
          writer.Write((Serial)0);
      }
    }
  }

  public enum TotalType
  {
    Gold,
    Items,
    Weight
  }

  [Flags]
  public enum ExpandFlag
  {
    None = 0x000,

    Name = 0x001,
    Items = 0x002,
    Bounce = 0x004,
    Holder = 0x008,
    Blessed = 0x010,
    TempFlag = 0x020,
    SaveFlag = 0x040,
    Weight = 0x080,
    Spawner = 0x100
  }

  public class Item : IHued, IComparable<Item>, ISerializable, ISpawnable, IPropertyListObject
  {
    private BufferWriter m_SaveBuffer;
    public BufferWriter SaveBuffer { get { return m_SaveBuffer; } }

    public const int QuestItemHue = 0x4EA; // Hmmmm... "for EA"?
    public static readonly List<Item> EmptyItems = new List<Item>();

    private static List<Item> m_DeltaQueue = new List<Item>();

    private static bool _processing;

    private static int m_OpenSlots;

    private CompactInfo m_CompactInfo;

    private ItemDelta m_DeltaFlags;
    private ImplFlag m_Flags;

    internal int m_TypeRef;

    [Constructible]
    public Item(int itemID = 0)
    {
      m_ItemID = itemID;
      Serial = Serial.NewItem;

      //m_Items = new ArrayList( 1 );
      Visible = true;
      Movable = true;
      Amount = 1;
      m_Map = Map.Internal;

      SetLastMoved();

      World.AddItem(this);

      Type ourType = GetType();
      m_TypeRef = World.m_ItemTypes.IndexOf(ourType);

      if (m_TypeRef == -1)
      {
        World.m_ItemTypes.Add(ourType);
        m_TypeRef = World.m_ItemTypes.Count - 1;
      }
      m_SaveBuffer = new BufferWriter(true);
    }

    public Item(Serial serial)
    {
      Serial = serial;

      Type ourType = GetType();
      m_TypeRef = World.m_ItemTypes.IndexOf(ourType);

      if (m_TypeRef == -1)
      {
        World.m_ItemTypes.Add(ourType);
        m_TypeRef = World.m_ItemTypes.Count - 1;
      }
      m_SaveBuffer = new BufferWriter(true);
    }

    public int TempFlags
    {
      get => LookupCompactInfo()?.m_TempFlags ?? 0;
      set
      {
        CompactInfo info = AcquireCompactInfo();

        info.m_TempFlags = value;

        if (info.m_TempFlags == 0)
          VerifyCompactInfo();
      }
    }

    public int SavedFlags
    {
      get => LookupCompactInfo()?.m_SavedFlags ?? 0;
      set
      {
        CompactInfo info = AcquireCompactInfo();

        info.m_SavedFlags = value;

        if (info.m_SavedFlags == 0)
          VerifyCompactInfo();
      }
    }

    /// <summary>
    ///   The <see cref="Mobile" /> who is currently <see cref="Mobile.Holding">holding</see> this item.
    /// </summary>
    public Mobile HeldBy
    {
      get => LookupCompactInfo()?.m_HeldBy;
      set
      {
        CompactInfo info = AcquireCompactInfo();

        info.m_HeldBy = value;

        if (info.m_HeldBy == null)
          VerifyCompactInfo();
      }
    }

    /// <summary>
    ///   Overridable. Determines whether the item will show <see cref="AddWeightProperty" />.
    /// </summary>
    public virtual bool DisplayWeight => Core.ML && (Movable || IsLockedDown || IsSecure || ItemData.Weight != 255);

    [CommandProperty(AccessLevel.GameMaster)]
    public LootType LootType
    {
      get => m_LootType;
      set
      {
        if (m_LootType != value)
        {
          m_LootType = value;

          if (DisplayLootType)
            InvalidateProperties();
        }
      }
    }

    public static TimeSpan DefaultDecayTime{ get; set; } = TimeSpan.FromHours(1.0);

    [CommandProperty(AccessLevel.GameMaster)]
    public virtual TimeSpan DecayTime => DefaultDecayTime;

    [CommandProperty(AccessLevel.GameMaster)]
    public virtual bool Decays => Movable && Visible;

    public DateTime LastMoved{ get; set; }

    [CommandProperty(AccessLevel.GameMaster)]
    public bool Stackable
    {
      get => GetFlag(ImplFlag.Stackable);
      set => SetFlag(ImplFlag.Stackable, value);
    }

    public Packet RemovePacket => StaticPacketHandlers.GetRemoveEntityPacket(this);
    public OPLInfo OPLPacket => StaticPacketHandlers.GetOPLInfoPacket(this);
    public ObjectPropertyList PropertyList => StaticPacketHandlers.GetOPLPacket(this);

    // World packets need to be invalidated when any of the following changes:
    //  - ItemID
    //  - Amount
    //  - Location
    //  - Hue
    //  - Packet Flags
    //  - Direction
    public Packet WorldPacket => StaticPacketHandlers.GetWorldItemPacket(this);
    public Packet WorldPacketSA => StaticPacketHandlers.GetWorldItemSAPacket(this);
    public Packet WorldPacketHS => StaticPacketHandlers.GetWorldItemHSPacket(this);

    [CommandProperty(AccessLevel.GameMaster)]
    public bool Visible
    {
      get => GetFlag(ImplFlag.Visible);
      set
      {
        if (GetFlag(ImplFlag.Visible) != value)
        {
          SetFlag(ImplFlag.Visible, value);
          ReleaseWorldPackets();

          if (m_Map != null)
          {
            Point3D worldLoc = GetWorldLocation();

            IPooledEnumerable<NetState> eable = m_Map.GetClientsInRange(worldLoc, GetMaxUpdateRange());

            foreach (NetState state in eable)
            {
              Mobile m = state.Mobile;

              if (!m.CanSee(this) && m.InRange(worldLoc, GetUpdateRange(m)))
                state.Send(RemovePacket);
            }

            eable.Free();
          }

          Delta(ItemDelta.Update);
        }
      }
    }

    [CommandProperty(AccessLevel.GameMaster)]
    public bool Movable
    {
      get => GetFlag(ImplFlag.Movable);
      set
      {
        if (GetFlag(ImplFlag.Movable) != value)
        {
          SetFlag(ImplFlag.Movable, value);
          ReleaseWorldPackets();
          Delta(ItemDelta.Update);
        }
      }
    }

    public virtual bool ForceShowProperties => false;

    public virtual bool HandlesOnMovement => false;

    public static int LockedDownFlag{ get; set; }

    public static int SecureFlag{ get; set; }

    public bool IsLockedDown
    {
      get => GetTempFlag(LockedDownFlag);
      set
      {
        SetTempFlag(LockedDownFlag, value);
        InvalidateProperties();
      }
    }

    public bool IsSecure
    {
      get => GetTempFlag(SecureFlag);
      set
      {
        SetTempFlag(SecureFlag, value);
        InvalidateProperties();
      }
    }

    public virtual bool IsVirtualItem => false;

    public virtual int LabelNumber
    {
      get
      {
        if (m_ItemID < 0x4000)
          return 1020000 + m_ItemID;

        return 1078872 + m_ItemID;
      }
    }

    [CommandProperty(AccessLevel.GameMaster)]
    public int TotalGold => GetTotal(TotalType.Gold);

    [CommandProperty(AccessLevel.GameMaster)]
    public int TotalItems => GetTotal(TotalType.Items);

    [CommandProperty(AccessLevel.GameMaster)]
    public int TotalWeight => GetTotal(TotalType.Weight);

    public virtual double DefaultWeight
    {
      get
      {
        if (m_ItemID < 0 || m_ItemID > TileData.MaxItemValue || this is BaseMulti)
          return 0;

        int weight = TileData.ItemTable[m_ItemID].Weight;

        if (weight == 255 || weight == 0)
          weight = 1;

        return weight;
      }
    }

    [CommandProperty(AccessLevel.Counselor, AccessLevel.GameMaster)]
    public double Weight
    {
      get
      {
        CompactInfo info = LookupCompactInfo();

        return info != null && info.m_Weight != -1 ? info.m_Weight : DefaultWeight;
      }
      set
      {
        if (Weight != value)
        {
          CompactInfo info = AcquireCompactInfo();

          int oldPileWeight = PileWeight;

          info.m_Weight = value;

          if (info.m_Weight == -1)
            VerifyCompactInfo();

          int newPileWeight = PileWeight;

          UpdateTotal(this, TotalType.Weight, newPileWeight - oldPileWeight);

          InvalidateProperties();
        }
      }
    }

    [CommandProperty(AccessLevel.Counselor, AccessLevel.GameMaster)]
    public int PileWeight => (int)Math.Ceiling(Weight * Amount);

    [Hue]
    [CommandProperty(AccessLevel.GameMaster)]
    public virtual int Hue
    {
      get => m_Hue;
      set
      {
        if (m_Hue != value)
        {
          m_Hue = value;
          ReleaseWorldPackets();

          Delta(ItemDelta.Update);
        }
      }
    }

    public virtual bool Nontransferable => QuestItem;

    [CommandProperty(AccessLevel.GameMaster)]
    public virtual Layer Layer
    {
      get => m_Layer;
      set
      {
        if (m_Layer != value)
        {
          m_Layer = value;

          Delta(ItemDelta.EquipOnly);
        }
      }
    }

    public List<Item> Items => LookupItems() ?? EmptyItems;

    [CommandProperty(AccessLevel.GameMaster)]
    public IEntity RootParent
    {
      get
      {
        IEntity p = m_Parent;

        while (p is Item item)
        {
          if (item.m_Parent == null) break;

          p = item.m_Parent;
        }

        return p;
      }
    }

    public bool NoMoveHS{ get; set; }

    public virtual int PhysicalResistance => 0;
    public virtual int FireResistance => 0;
    public virtual int ColdResistance => 0;
    public virtual int PoisonResistance => 0;
    public virtual int EnergyResistance => 0;

    [CommandProperty(AccessLevel.GameMaster)]
    public virtual int ItemID
    {
      get => m_ItemID;
      set
      {
        if (m_ItemID != value)
        {
          int oldPileWeight = PileWeight;

          m_ItemID = value;
          ReleaseWorldPackets();

          int newPileWeight = PileWeight;

          UpdateTotal(this, TotalType.Weight, newPileWeight - oldPileWeight);

          InvalidateProperties();
          Delta(ItemDelta.Update);
        }
      }
    }

    public virtual string DefaultName => null;

    [CommandProperty(AccessLevel.GameMaster)]
    public string Name
    {
      get => LookupCompactInfo()?.m_Name ?? DefaultName;
      set
      {
        if (value == null || value != DefaultName)
        {
          CompactInfo info = AcquireCompactInfo();

          info.m_Name = value;

          if (info.m_Name == null)
            VerifyCompactInfo();

          InvalidateProperties();
        }
      }
    }

    [CommandProperty(AccessLevel.GameMaster, AccessLevel.Developer)]
    public IEntity Parent
    {
      get => m_Parent;
      set
      {
        if (m_Parent == value)
          return;

        IEntity oldParent = m_Parent;

        m_Parent = value;

        if (m_Map != null)
        {
          if (oldParent != null && m_Parent == null)
            m_Map.OnEnter(this);
          else if (m_Parent != null)
            m_Map.OnLeave(this);
        }
      }
    }

    [CommandProperty(AccessLevel.GameMaster)]
    public LightType Light
    {
      get => (LightType)m_Direction;
      set
      {
        if ((LightType)m_Direction != value)
        {
          m_Direction = (Direction)value;
          ReleaseWorldPackets();

          Delta(ItemDelta.Update);
        }
      }
    }

    [CommandProperty(AccessLevel.GameMaster)]
    public Direction Direction
    {
      get => m_Direction;
      set
      {
        if (m_Direction != value)
        {
          m_Direction = value;
          ReleaseWorldPackets();

          Delta(ItemDelta.Update);
        }
      }
    }

    [CommandProperty(AccessLevel.GameMaster)]
    public int Amount
    {
      get => m_Amount;
      set
      {
        int oldValue = m_Amount;

        if (oldValue != value)
        {
          int oldPileWeight = PileWeight;

          m_Amount = value;
          ReleaseWorldPackets();

          int newPileWeight = PileWeight;

          UpdateTotal(this, TotalType.Weight, newPileWeight - oldPileWeight);

          OnAmountChange(oldValue);

          Delta(ItemDelta.Update);

          if (oldValue > 1 || value > 1)
            InvalidateProperties();

          if (!Stackable && m_Amount > 1)
            Console.WriteLine("Warning: 0x{0:X}: Amount changed for non-stackable item '{2}'. ({1})",
              Serial.Value, m_Amount, GetType().Name);
        }
      }
    }

    public virtual bool HandlesOnSpeech => false;

    public virtual bool BlocksFit => false;

    public bool InSecureTrade => GetSecureTradeCont() != null;

    public ItemData ItemData => TileData.ItemTable[m_ItemID & TileData.MaxItemValue];

    public virtual bool CanTarget => true;
    public virtual bool DisplayLootType => true;

    public static bool ScissorCopyLootType{ get; set; }

    [CommandProperty(AccessLevel.GameMaster)]
    public bool QuestItem
    {
      get => GetFlag(ImplFlag.QuestItem);
      set
      {
        SetFlag(ImplFlag.QuestItem, value);

        InvalidateProperties();

        ReleaseWorldPackets();

        Delta(ItemDelta.Update);
      }
    }

    public bool Insured
    {
      get => GetFlag(ImplFlag.Insured);
      set
      {
        SetFlag(ImplFlag.Insured, value);
        InvalidateProperties();
      }
    }

    public bool PaidInsurance
    {
      get => GetFlag(ImplFlag.PaidInsurance);
      set => SetFlag(ImplFlag.PaidInsurance, value);
    }

    public Mobile BlessedFor
    {
      get => LookupCompactInfo()?.m_BlessedFor;
      set
      {
        CompactInfo info = AcquireCompactInfo();

        info.m_BlessedFor = value;

        if (info.m_BlessedFor == null)
          VerifyCompactInfo();

        InvalidateProperties();
      }
    }

    int IComparable<IEntity>.CompareTo(IEntity other) => other == null ? -1 : Serial.CompareTo(other.Serial);

    public int CompareTo(Item other) => other == null ? -1 : Serial.CompareTo(other.Serial);

    /// <summary>
    ///   Moves the Item to a given <paramref name="location" /> and <paramref name="map" />.
    /// </summary>
    public void MoveToWorld(Point3D location, Map map)
    {
      if (Deleted)
        return;

      Point3D oldLocation = GetWorldLocation();
      Point3D oldRealLocation = m_Location;

      SetLastMoved();

      if (Parent is Mobile mobile)
        mobile.RemoveItem(this);
      else if (Parent is Item item)
        item.RemoveItem(this);

      if (m_Map != map)
      {
        Map old = m_Map;

        if (m_Map != null)
        {
          m_Map.OnLeave(this);

          if (oldLocation.m_X != 0)
          {
            IPooledEnumerable<NetState> eable = m_Map.GetClientsInRange(oldLocation, GetMaxUpdateRange());

            foreach (NetState state in eable)
            {
              Mobile m = state.Mobile;

              if (m.InRange(oldLocation, GetUpdateRange(m)))
                state.Send(RemovePacket);
            }

            eable.Free();
          }
        }

        m_Location = location;
        OnLocationChange(oldRealLocation);

        ReleaseWorldPackets();

        List<Item> items = LookupItems();

        if (items != null)
          for (int i = 0; i < items.Count; ++i)
            items[i].Map = map;

        m_Map = map;
        m_Map?.OnEnter(this);

        OnMapChange();

        if (m_Map != null)
        {
          IPooledEnumerable<NetState> eable = m_Map.GetClientsInRange(m_Location, GetMaxUpdateRange());

          foreach (NetState state in eable)
          {
            Mobile m = state.Mobile;

            if (m.CanSee(this) && m.InRange(m_Location, GetUpdateRange(m)))
              SendInfoTo(state);
          }

          eable.Free();
        }

        RemDelta(ItemDelta.Update);

        if (old == null || old == Map.Internal)
          InvalidateProperties();
      }
      else if (m_Map != null)
      {
        IPooledEnumerable<NetState> eable;

        if (oldLocation.m_X != 0)
        {
          eable = m_Map.GetClientsInRange(oldLocation, GetMaxUpdateRange());

          foreach (NetState state in eable)
          {
            Mobile m = state.Mobile;

            if (!m.InRange(location, GetUpdateRange(m))) state.Send(RemovePacket);
          }

          eable.Free();
        }

        Point3D oldInternalLocation = m_Location;

        m_Location = location;
        OnLocationChange(oldRealLocation);

        ReleaseWorldPackets();

        eable = m_Map.GetClientsInRange(m_Location, GetMaxUpdateRange());

        foreach (NetState state in eable)
        {
          Mobile m = state.Mobile;

          if (m.CanSee(this) && m.InRange(m_Location, GetUpdateRange(m)))
            SendInfoTo(state);
        }

        eable.Free();

        m_Map.OnMove(oldInternalLocation, this);

        RemDelta(ItemDelta.Update);
      }
      else
      {
        Map = map;
        Location = location;
      }
    }

    /// <summary>
    ///   Has the item been deleted?
    /// </summary>
    public bool Deleted => GetFlag(ImplFlag.Deleted);

    [CommandProperty(AccessLevel.Counselor, AccessLevel.GameMaster)]
    public Map Map
    {
      get => m_Map;
      set
      {
        if (m_Map != value)
        {
          Map old = m_Map;

          if (m_Map != null && m_Parent == null)
          {
            m_Map.OnLeave(this);
            SendRemovePacket();
          }

          List<Item> items = LookupItems();

          if (items != null)
            for (int i = 0; i < items.Count; ++i)
              items[i].Map = value;

          m_Map = value;

          if (m_Map != null && m_Parent == null)
            m_Map.OnEnter(this);

          Delta(ItemDelta.Update);

          OnMapChange();

          if (old == null || old == Map.Internal)
            InvalidateProperties();
        }
      }
    }

    public virtual void ProcessDelta()
    {
      ItemDelta flags = m_DeltaFlags;

      SetFlag(ImplFlag.InQueue, false);
      m_DeltaFlags = ItemDelta.None;

      Map map = m_Map;

      if (map == null || Deleted)
        return;

      Point3D worldLoc = GetWorldLocation();
      bool update = (flags & ItemDelta.Update) != 0;

      if (update && m_Parent is Container contParent && !contParent.IsPublicContainer)
      {
        Mobile rootParent = contParent.RootParent as Mobile;
        Mobile tradeRecip = null;

        if (rootParent != null)
        {
          NetState ns = rootParent.NetState;

          if (ns != null)
            if (rootParent.CanSee(this) && rootParent.InRange(worldLoc, GetUpdateRange(rootParent)))
            {
              if (ns.ContainerGridLines)
                ns.Send(new ContainerContentUpdate6017(this));
              else
                ns.Send(new ContainerContentUpdate(this));

              if (ObjectPropertyList.Enabled)
                ns.Send(OPLPacket);
            }
        }

        SecureTrade st = GetSecureTradeCont()?.Trade;

        if (st != null)
        {
          Mobile test = st.From.Mobile;

          if (test != null && test != rootParent)
            tradeRecip = test;

          test = st.To.Mobile;

          if (test != null && test != rootParent)
            tradeRecip = test;

          NetState ns = tradeRecip?.NetState;

          if (ns != null && tradeRecip.CanSee(this) && tradeRecip.InRange(worldLoc, GetUpdateRange(tradeRecip)))
          {
            if (ns.ContainerGridLines)
              ns.Send(new ContainerContentUpdate6017(this));
            else
              ns.Send(new ContainerContentUpdate(this));

            if (ObjectPropertyList.Enabled)
              ns.Send(OPLPacket);
          }
        }

        List<Mobile> openers = contParent.Openers;

        if (openers != null)
          lock (openers)
          {
            for (int i = 0; i < openers.Count; ++i)
            {
              Mobile mob = openers[i];

              int range = GetUpdateRange(mob);

              if (mob.Map != map || !mob.InRange(worldLoc, range))
              {
                openers.RemoveAt(i--);
              }
              else
              {
                if (mob == rootParent || mob == tradeRecip)
                  continue;

                NetState ns = mob.NetState;

                if (ns != null && mob.CanSee(this))
                {
                  if (ns.ContainerGridLines)
                    ns.Send(new ContainerContentUpdate6017(this));
                  else
                    ns.Send(new ContainerContentUpdate(this));

                  if (ObjectPropertyList.Enabled)
                    ns.Send(OPLPacket);
                }
              }
            }

            if (openers.Count == 0)
              contParent.Openers = null;
          }

        return;
      }

      Packet p = null;

      IPooledEnumerable<NetState> eable = map.GetClientsInRange(worldLoc, GetMaxUpdateRange());

      foreach (NetState state in eable)
      {
        Mobile m = state.Mobile;

        if (m.CanSee(this) && m.InRange(worldLoc, GetUpdateRange(m)))
        {
          if (update)
          {
            if (m_Parent == null)
              SendInfoTo(state, ObjectPropertyList.Enabled);
            else
            {
              if (p != null)
                state.Send(p);
              else if (m_Parent is Item)
              {
                if (state.ContainerGridLines)
                  state.Send(new ContainerContentUpdate6017(this));
                else
                  state.Send(new ContainerContentUpdate(this));
              }
              else if (m_Parent is Mobile)
              {
                p = new EquipUpdate(this);
                p.Acquire();

                state.Send(p);
              }

              if (ObjectPropertyList.Enabled)
                state.Send(OPLPacket);
            }
          }
          else if ((flags & ItemDelta.EquipOnly) != 0 && m_Parent is Mobile)
          {
            state.Send(p ?? (p = Packet.Acquire(new EquipUpdate(this))));

            if (ObjectPropertyList.Enabled)
              state.Send(OPLPacket);
          } else if (ObjectPropertyList.Enabled && (flags & ItemDelta.Properties) != 0)
            state.Send(OPLPacket);
        }
      }

      Packet.Release(p);
      eable.Free();
    }

    public virtual void Delete()
    {
      if (Deleted || !World.OnDelete(this))
        return;

      OnDelete();

      List<Item> items = LookupItems();

      if (items != null)
        for (int i = items.Count - 1; i >= 0; --i)
          if (i < items.Count)
            items[i].OnParentDeleted(this);

      SendRemovePacket();

      SetFlag(ImplFlag.Deleted, true);

      if (Parent is Mobile mobile)
        mobile.RemoveItem(this);
      else if (Parent is Item item)
        item.RemoveItem(this);

      ClearBounce();

      if (m_Map != null)
      {
        if (m_Parent == null)
          m_Map.OnLeave(this);
        m_Map = null;
      }

      World.RemoveItem(this);

      OnAfterDelete();

      FreeCache();
    }

    [CommandProperty(AccessLevel.Counselor)]
    public Serial Serial{ get; }

    public virtual int HuedItemID => m_ItemID;

    int ISerializable.TypeReference => m_TypeRef;

    uint ISerializable.SerialIdentity => Serial;

    public void Serialize()
    {
      SaveBuffer.Flush();
      Serialize(SaveBuffer);
    }
    public virtual void Serialize(IGenericWriter writer)
    {
      writer.Write(9); // version

      SaveFlag flags = SaveFlag.None;

      int x = m_Location.m_X, y = m_Location.m_Y, z = m_Location.m_Z;

      if (x != 0 || y != 0 || z != 0)
      {
        if (x >= short.MinValue && x <= short.MaxValue && y >= short.MinValue && y <= short.MaxValue &&
            z >= sbyte.MinValue && z <= sbyte.MaxValue)
        {
          if (x != 0 || y != 0)
          {
            if (x >= byte.MinValue && x <= byte.MaxValue && y >= byte.MinValue && y <= byte.MaxValue)
              flags |= SaveFlag.LocationByteXY;
            else
              flags |= SaveFlag.LocationShortXY;
          }

          if (z != 0)
            flags |= SaveFlag.LocationSByteZ;
        }
        else
        {
          flags |= SaveFlag.LocationFull;
        }
      }

      CompactInfo info = LookupCompactInfo();
      List<Item> items = LookupItems();

      if (m_Direction != Direction.North)
        flags |= SaveFlag.Direction;
      if (info?.m_Bounce != null)
        flags |= SaveFlag.Bounce;
      if (m_LootType != LootType.Regular)
        flags |= SaveFlag.LootType;
      if (m_ItemID != 0)
        flags |= SaveFlag.ItemID;
      if (m_Hue != 0)
        flags |= SaveFlag.Hue;
      if (m_Amount != 1)
        flags |= SaveFlag.Amount;
      if (m_Layer != Layer.Invalid)
        flags |= SaveFlag.Layer;
      if (info?.m_Name != null)
        flags |= SaveFlag.Name;
      if (m_Parent != null)
        flags |= SaveFlag.Parent;
      if (items != null && items.Count > 0)
        flags |= SaveFlag.Items;
      if (m_Map != Map.Internal)
        flags |= SaveFlag.Map;
      //if ( m_InsuredFor != null && !m_InsuredFor.Deleted )
      //flags |= SaveFlag.InsuredFor;

      if (info != null)
      {
        if (info.m_BlessedFor?.Deleted == false)
          flags |= SaveFlag.BlessedFor;
        if (info.m_HeldBy?.Deleted == false)
          flags |= SaveFlag.HeldBy;
        if (info.m_SavedFlags != 0)
          flags |= SaveFlag.SavedFlags;
      }

      if (info == null || info.m_Weight == -1.0)
      {
        flags |= SaveFlag.NullWeight;
      }
      else
      {
        if (info.m_Weight == 0.0)
        {
          flags |= SaveFlag.WeightIs0;
        }
        else if (info.m_Weight != 1.0)
        {
          if (info.m_Weight == (int)info.m_Weight)
            flags |= SaveFlag.IntWeight;
          else
            flags |= SaveFlag.WeightNot1or0;
        }
      }

      ImplFlag implFlags = m_Flags & (ImplFlag.Visible | ImplFlag.Movable | ImplFlag.Stackable | ImplFlag.Insured |
                                      ImplFlag.PaidInsurance | ImplFlag.QuestItem);

      if (implFlags != (ImplFlag.Visible | ImplFlag.Movable))
        flags |= SaveFlag.ImplFlags;

      writer.Write((int)flags);

      /* begin last moved time optimization */
      long ticks = LastMoved.Ticks;
      long now = DateTime.UtcNow.Ticks;

      TimeSpan d;

      try
      {
        d = new TimeSpan(ticks - now);
      }
      catch
      {
        if (ticks < now) d = TimeSpan.MaxValue;
        else d = TimeSpan.MaxValue;
      }

      double minutes = -d.TotalMinutes;

      if (minutes < int.MinValue)
        minutes = int.MinValue;
      else if (minutes > int.MaxValue)
        minutes = int.MaxValue;

      writer.WriteEncodedInt((int)minutes);
      /* end */

      if (GetSaveFlag(flags, SaveFlag.Direction))
        writer.Write((byte)m_Direction);

      if (GetSaveFlag(flags, SaveFlag.Bounce))
        BounceInfo.Serialize(info.m_Bounce, writer);

      if (GetSaveFlag(flags, SaveFlag.LootType))
        writer.Write((byte)m_LootType);

      if (GetSaveFlag(flags, SaveFlag.LocationFull))
      {
        writer.WriteEncodedInt(x);
        writer.WriteEncodedInt(y);
        writer.WriteEncodedInt(z);
      }
      else
      {
        if (GetSaveFlag(flags, SaveFlag.LocationByteXY))
        {
          writer.Write((byte)x);
          writer.Write((byte)y);
        }
        else if (GetSaveFlag(flags, SaveFlag.LocationShortXY))
        {
          writer.Write((short)x);
          writer.Write((short)y);
        }

        if (GetSaveFlag(flags, SaveFlag.LocationSByteZ))
          writer.Write((sbyte)z);
      }

      if (GetSaveFlag(flags, SaveFlag.ItemID))
        writer.WriteEncodedInt(m_ItemID);

      if (GetSaveFlag(flags, SaveFlag.Hue))
        writer.WriteEncodedInt(m_Hue);

      if (GetSaveFlag(flags, SaveFlag.Amount))
        writer.WriteEncodedInt(m_Amount);

      if (GetSaveFlag(flags, SaveFlag.Layer))
        writer.Write((byte)m_Layer);

      if (GetSaveFlag(flags, SaveFlag.Name))
        writer.Write(info.m_Name);

      if (GetSaveFlag(flags, SaveFlag.Parent))
      {
        if (m_Parent?.Deleted == false)
          writer.Write(m_Parent.Serial);
        else
          writer.Write(Serial.MinusOne);
      }

      if (GetSaveFlag(flags, SaveFlag.Items))
        writer.Write(items, false);

      if (GetSaveFlag(flags, SaveFlag.IntWeight))
        writer.WriteEncodedInt((int)info.m_Weight);
      else if (GetSaveFlag(flags, SaveFlag.WeightNot1or0))
        writer.Write(info.m_Weight);

      if (GetSaveFlag(flags, SaveFlag.Map))
        writer.Write(m_Map);

      if (GetSaveFlag(flags, SaveFlag.ImplFlags))
        writer.WriteEncodedInt((int)implFlags);

      if (GetSaveFlag(flags, SaveFlag.InsuredFor))
        writer.Write((Mobile)null);

      if (GetSaveFlag(flags, SaveFlag.BlessedFor))
        writer.Write(info.m_BlessedFor);

      if (GetSaveFlag(flags, SaveFlag.HeldBy))
        writer.Write(info.m_HeldBy);

      if (GetSaveFlag(flags, SaveFlag.SavedFlags))
        writer.WriteEncodedInt(info.m_SavedFlags);
    }

    public ISpawner Spawner
    {
      get => LookupCompactInfo()?.m_Spawner;
      set
      {
        CompactInfo info = AcquireCompactInfo();

        info.m_Spawner = value;

        if (info.m_Spawner == null)
          VerifyCompactInfo();
      }
    }

    public virtual void OnBeforeSpawn(Point3D location, Map m)
    {
    }

    public virtual void OnAfterSpawn()
    {
    }

    public ExpandFlag GetExpandFlags()
    {
      CompactInfo info = LookupCompactInfo();

      ExpandFlag flags = 0;

      if (info != null)
      {
        if (info.m_BlessedFor != null)
          flags |= ExpandFlag.Blessed;

        if (info.m_Bounce != null)
          flags |= ExpandFlag.Bounce;

        if (info.m_HeldBy != null)
          flags |= ExpandFlag.Holder;

        if (info.m_Items != null)
          flags |= ExpandFlag.Items;

        if (info.m_Name != null)
          flags |= ExpandFlag.Name;

        if (info.m_Spawner != null)
          flags |= ExpandFlag.Spawner;

        if (info.m_SavedFlags != 0)
          flags |= ExpandFlag.SaveFlag;

        if (info.m_TempFlags != 0)
          flags |= ExpandFlag.TempFlag;

        if (info.m_Weight != -1)
          flags |= ExpandFlag.Weight;
      }

      return flags;
    }

    private CompactInfo LookupCompactInfo() => m_CompactInfo;

    private CompactInfo AcquireCompactInfo() => m_CompactInfo ?? (m_CompactInfo = new CompactInfo());

    private void ReleaseCompactInfo()
    {
      m_CompactInfo = null;
    }

    private void VerifyCompactInfo()
    {
      CompactInfo info = m_CompactInfo;

      if (info == null)
        return;

      bool isValid = info.m_Name != null
                     || info.m_Items != null
                     || info.m_Bounce != null
                     || info.m_HeldBy != null
                     || info.m_BlessedFor != null
                     || info.m_Spawner != null
                     || info.m_TempFlags != 0
                     || info.m_SavedFlags != 0
                     || info.m_Weight != -1;

      if (!isValid)
        ReleaseCompactInfo();
    }

    public List<Item> LookupItems()
    {
      if (this is Container container)
        return container.m_Items;

      return LookupCompactInfo()?.m_Items;
    }

    public List<Item> AcquireItems()
    {
      if (this is Container cont)
        return cont.m_Items ?? (cont.m_Items = new List<Item>());

      CompactInfo info = AcquireCompactInfo();
      return info.m_Items ?? (info.m_Items = new List<Item>());
    }

    private void SetFlag(ImplFlag flag, bool value)
    {
      if (value)
        m_Flags |= flag;
      else
        m_Flags &= ~flag;
    }

    private bool GetFlag(ImplFlag flag) => (m_Flags & flag) != 0;

    public BounceInfo GetBounce() => LookupCompactInfo()?.m_Bounce;

    public void RecordBounce()
    {
      AcquireCompactInfo().m_Bounce = new BounceInfo(this);
    }

    public void ClearBounce()
    {
      CompactInfo info = LookupCompactInfo();

      BounceInfo bounce = info?.m_Bounce;

      if (bounce == null)
        return;

      info.m_Bounce = null;

      if (bounce.m_Parent is Item parentItem)
      {
        if (!parentItem.Deleted)
          parentItem.OnItemBounceCleared(this);
      }
      else if (bounce.m_Parent is Mobile parentMobile)
      {
        if (!parentMobile.Deleted)
          parentMobile.OnItemBounceCleared(this);
      }

      VerifyCompactInfo();
    }

    /// <summary>
    ///   Overridable. Virtual event invoked when a client, <paramref name="from" />, invokes a 'help request' for the Item.
    ///   Seemingly no longer functional in newer clients.
    /// </summary>
    public virtual void OnHelpRequest(Mobile from)
    {
    }

    /// <summary>
    ///   Overridable. Method checked to see if the item can be traded.
    /// </summary>
    /// <returns>True if the trade is allowed, false if not.</returns>
    public virtual bool AllowSecureTrade(Mobile from, Mobile to, Mobile newOwner, bool accepted) => true;

    /// <summary>
    ///   Overridable. Virtual event invoked when a trade has completed, either successfully or not.
    /// </summary>
    public virtual void OnSecureTrade(Mobile from, Mobile to, Mobile newOwner, bool accepted)
    {
    }

    /// <summary>
    ///   Overridable. Method checked to see if the elemental resistances of this Item conflict with another Item on the
    ///   <see cref="Mobile" />.
    /// </summary>
    /// <returns>
    ///   <list type="table">
    ///     <item>
    ///       <term>True</term>
    ///       <description>
    ///         There is a confliction. The elemental resistance bonuses of this Item should not be applied to the
    ///         <see cref="Mobile" />
    ///       </description>
    ///     </item>
    ///     <item>
    ///       <term>False</term>
    ///       <description>There is no confliction. The bonuses should be applied.</description>
    ///     </item>
    ///   </list>
    /// </returns>
    public virtual bool CheckPropertyConfliction(Mobile m) => false;

    /// <summary>
    ///   Overridable. Sends the <see cref="PropertyList">object property list</see> to <paramref name="from" />.
    /// </summary>
    public virtual void SendPropertiesTo(Mobile from)
    {
      from.Send(PropertyList);
    }

    /// <summary>
    ///   Overridable. Adds the name of this item to the given <see cref="ObjectPropertyList" />. This method should be overridden
    ///   if the item requires a complex naming format.
    /// </summary>
    public virtual void AddNameProperty(ObjectPropertyList list)
    {
      string name = Name;

      if (name == null)
      {
        if (m_Amount <= 1)
          list.Add(LabelNumber);
        else
          list.Add(1050039, "{0}\t#{1}", m_Amount, LabelNumber); // ~1_NUMBER~ ~2_ITEMNAME~
      }
      else
      {
        if (m_Amount <= 1)
          list.Add(name);
        else
          list.Add(1050039, "{0}\t{1}", m_Amount, Name); // ~1_NUMBER~ ~2_ITEMNAME~
      }
    }

    /// <summary>
    ///   Overridable. Adds the loot type of this item to the given <see cref="ObjectPropertyList" />. By default, this will be
    ///   either 'blessed', 'cursed', or 'insured'.
    /// </summary>
    public virtual void AddLootTypeProperty(ObjectPropertyList list)
    {
      if (m_LootType == LootType.Blessed)
        list.Add(1038021); // blessed
      else if (m_LootType == LootType.Cursed)
        list.Add(1049643); // cursed
      else if (Insured)
        list.Add(1061682); // <b>insured</b>
    }

    /// <summary>
    ///   Overridable. Adds any elemental resistances of this item to the given <see cref="ObjectPropertyList" />.
    /// </summary>
    public virtual void AddResistanceProperties(ObjectPropertyList list)
    {
      int v = PhysicalResistance;

      if (v != 0)
        list.Add(1060448, v.ToString()); // physical resist ~1_val~%

      v = FireResistance;

      if (v != 0)
        list.Add(1060447, v.ToString()); // fire resist ~1_val~%

      v = ColdResistance;

      if (v != 0)
        list.Add(1060445, v.ToString()); // cold resist ~1_val~%

      v = PoisonResistance;

      if (v != 0)
        list.Add(1060449, v.ToString()); // poison resist ~1_val~%

      v = EnergyResistance;

      if (v != 0)
        list.Add(1060446, v.ToString()); // energy resist ~1_val~%
    }

    /// <summary>
    ///   Overridable. Displays cliloc 1072788-1072789.
    /// </summary>
    public virtual void AddWeightProperty(ObjectPropertyList list)
    {
      int weight = PileWeight + TotalWeight;

      if (weight == 1)
        list.Add(1072788, weight.ToString()); //Weight: ~1_WEIGHT~ stone
      else
        list.Add(1072789, weight.ToString()); //Weight: ~1_WEIGHT~ stones
    }

    /// <summary>
    ///   Overridable. Adds header properties. By default, this invokes <see cref="AddNameProperty" />,
    ///   <see cref="AddBlessedForProperty" /> (if applicable), and <see cref="AddLootTypeProperty" /> (if
    ///   <see cref="DisplayLootType" />).
    /// </summary>
    public virtual void AddNameProperties(ObjectPropertyList list)
    {
      AddNameProperty(list);

      if (IsSecure)
        AddSecureProperty(list);
      else if (IsLockedDown)
        AddLockedDownProperty(list);

      Mobile blessedFor = BlessedFor;

      if (blessedFor?.Deleted == false)
        AddBlessedForProperty(list, blessedFor);

      if (DisplayLootType)
        AddLootTypeProperty(list);

      if (DisplayWeight)
        AddWeightProperty(list);

      if (QuestItem)
        AddQuestItemProperty(list);


      AppendChildNameProperties(list);
    }

    /// <summary>
    ///   Overridable. Adds the "Quest Item" property to the given <see cref="ObjectPropertyList" />.
    /// </summary>
    public virtual void AddQuestItemProperty(ObjectPropertyList list)
    {
      list.Add(1072351); // Quest Item
    }

    /// <summary>
    ///   Overridable. Adds the "Locked Down & Secure" property to the given <see cref="ObjectPropertyList" />.
    /// </summary>
    public virtual void AddSecureProperty(ObjectPropertyList list)
    {
      list.Add(501644); // locked down & secure
    }

    /// <summary>
    ///   Overridable. Adds the "Locked Down" property to the given <see cref="ObjectPropertyList" />.
    /// </summary>
    public virtual void AddLockedDownProperty(ObjectPropertyList list)
    {
      list.Add(501643); // locked down
    }

    /// <summary>
    ///   Overridable. Adds the "Blessed for ~1_NAME~" property to the given <see cref="ObjectPropertyList" />.
    /// </summary>
    public virtual void AddBlessedForProperty(ObjectPropertyList list, Mobile m)
    {
      list.Add(1062203, "{0}", m.Name); // Blessed for ~1_NAME~
    }

    /// <summary>
    ///   Overridable. Fills an <see cref="ObjectPropertyList" /> with everything applicable. By default, this invokes
    ///   <see cref="AddNameProperties" />, then <see cref="Item.GetChildProperties">Item.GetChildProperties</see> or
    ///   <see cref="Mobile.GetChildProperties">Mobile.GetChildProperties</see>. This method should be overridden to add any custom
    ///   properties.
    /// </summary>
    public virtual void GetProperties(ObjectPropertyList list)
    {
      AddNameProperties(list);
    }

    /// <summary>
    ///   Overridable. Event invoked when a child (<paramref name="item" />) is building it's <see cref="ObjectPropertyList" />.
    ///   Recursively calls <see cref="Item.GetChildProperties">Item.GetChildProperties</see> or
    ///   <see cref="Mobile.GetChildProperties">Mobile.GetChildProperties</see>.
    /// </summary>
    public virtual void GetChildProperties(ObjectPropertyList list, Item item)
    {
      if (m_Parent is Item parentItem)
        parentItem.GetChildProperties(list, item);
      else if (m_Parent is Mobile parentMobile)
        parentMobile.GetChildProperties(list, item);
    }

    /// <summary>
    ///   Overridable. Event invoked when a child (<paramref name="item" />) is building it's Name <see cref="ObjectPropertyList" />
    ///   . Recursively calls <see cref="Item.GetChildProperties">Item.GetChildNameProperties</see> or
    ///   <see cref="Mobile.GetChildProperties">Mobile.GetChildNameProperties</see>.
    /// </summary>
    public virtual void GetChildNameProperties(ObjectPropertyList list, Item item)
    {
      if (m_Parent is Item parentItem)
        parentItem.GetChildNameProperties(list, item);
      else if (m_Parent is Mobile parentMobile)
        parentMobile.GetChildNameProperties(list, item);
    }

    public virtual bool IsChildVisibleTo(Mobile m, Item child) => true;

    public void Bounce(Mobile from)
    {
      if (m_Parent is Item item)
        item.RemoveItem(this);
      else if (m_Parent is Mobile mobile)
        mobile.RemoveItem(this);

      m_Parent = null;

      BounceInfo bounce = GetBounce();

      if (bounce != null)
      {
        IEntity parent = bounce.m_Parent;

        if (parent?.Deleted != false)
        {
          MoveToWorld(bounce.m_WorldLoc, bounce.m_Map);
        }
        else if (parent is Item p)
        {
          IEntity root = p.RootParent;

          if (p.IsAccessibleTo(from) &&
              (!(root is Mobile mobileRoot) || mobileRoot.CheckNonlocalDrop(from, this, p)))
          {
            Location = bounce.m_Location;
            p.AddItem(this);
          }
          else
          {
            MoveToWorld(from.Location, from.Map);
          }
        }
        else if (parent is Mobile parentMobile)
        {
          if (!parentMobile.EquipItem(this))
            MoveToWorld(bounce.m_WorldLoc, bounce.m_Map);
        }
        else
        {
          MoveToWorld(bounce.m_WorldLoc, bounce.m_Map);
        }

        ClearBounce();
      }
      else
      {
        MoveToWorld(from.Location, from.Map);
      }
    }

    /// <summary>
    ///   Overridable. Method checked to see if this item may be equipped while casting a spell. By default, this returns false. It
    ///   is overridden on spellbook and spell channeling weapons or shields.
    /// </summary>
    /// <returns>True if it may, false if not.</returns>
    /// <example>
    ///   <code>
    /// 	public override bool AllowEquippedCast( Mobile from )
    /// 	{
    /// 		if ( from.Int &gt;= 100 )
    /// 			return true;
    ///
    /// 		return base.AllowEquippedCast( from );
    ///  }</code>
    ///   When placed in an Item script, the item may be cast when equipped if the <paramref name="from" /> has 100 or more
    ///   intelligence. Otherwise, it will drop to their backpack.
    /// </example>
    public virtual bool AllowEquippedCast(Mobile from) => false;

    public virtual bool CheckConflictingLayer(Mobile m, Item item, Layer layer) => m_Layer == layer;

    public virtual bool CanEquip(Mobile m) => m_Layer != Layer.Invalid && m.FindItemOnLayer(m_Layer) == null;

    public virtual void GetChildContextMenuEntries(Mobile from, List<ContextMenuEntry> list, Item item)
    {
      if (m_Parent is Item parentItem)
        parentItem.GetChildContextMenuEntries(from, list, item);
      else if (m_Parent is Mobile parentMobile)
        parentMobile.GetChildContextMenuEntries(from, list, item);
    }

    public virtual void GetContextMenuEntries(Mobile from, List<ContextMenuEntry> list)
    {
      if (m_Parent is Item item)
        item.GetChildContextMenuEntries(from, list, this);
      else if (m_Parent is Mobile mobile)
        mobile.GetChildContextMenuEntries(from, list, this);
    }

    public virtual bool VerifyMove(Mobile from) => Movable;

    public virtual DeathMoveResult OnParentDeath(Mobile parent)
    {
      if (!Movable)
        return DeathMoveResult.RemainEquipped;
      if (parent.KeepsItemsOnDeath)
        return DeathMoveResult.MoveToBackpack;
      if (CheckBlessed(parent))
        return DeathMoveResult.MoveToBackpack;
      if (CheckNewbied() && parent.Kills < 5)
        return DeathMoveResult.MoveToBackpack;
      if (parent.Player && Nontransferable)
        return DeathMoveResult.MoveToBackpack;

      return DeathMoveResult.MoveToCorpse;
    }

    public virtual DeathMoveResult OnInventoryDeath(Mobile parent)
    {
      if (!Movable)
        return DeathMoveResult.MoveToBackpack;
      if (parent.KeepsItemsOnDeath)
        return DeathMoveResult.MoveToBackpack;
      if (CheckBlessed(parent))
        return DeathMoveResult.MoveToBackpack;
      if (CheckNewbied() && parent.Kills < 5)
        return DeathMoveResult.MoveToBackpack;
      if (parent.Player && Nontransferable)
        return DeathMoveResult.MoveToBackpack;

      return DeathMoveResult.MoveToCorpse;
    }

    /// <summary>
    ///   Moves the Item to <paramref name="location" />. The Item does not change maps.
    /// </summary>
    public virtual void MoveToWorld(Point3D location)
    {
      MoveToWorld(location, m_Map);
    }

    public void LabelTo(Mobile to, int number)
    {
      to.Send(new MessageLocalized(Serial, m_ItemID, MessageType.Label, 0x3B2, 3, number, "", ""));
    }

    public void LabelTo(Mobile to, int number, string args)
    {
      to.Send(new MessageLocalized(Serial, m_ItemID, MessageType.Label, 0x3B2, 3, number, "", args));
    }

    public void LabelTo(Mobile to, string text)
    {
      to.Send(new UnicodeMessage(Serial, m_ItemID, MessageType.Label, 0x3B2, 3, "ENU", "", text));
    }

    public void LabelTo(Mobile to, string format, params object[] args)
    {
      LabelTo(to, string.Format(format, args));
    }

    public void LabelToAffix(Mobile to, int number, AffixType type, string affix)
    {
      to.Send(new MessageLocalizedAffix(Serial, m_ItemID, MessageType.Label, 0x3B2, 3, number, "", type, affix, ""));
    }

    public void LabelToAffix(Mobile to, int number, AffixType type, string affix, string args)
    {
      to.Send(new MessageLocalizedAffix(Serial, m_ItemID, MessageType.Label, 0x3B2, 3, number, "", type, affix, args));
    }

    public virtual void LabelLootTypeTo(Mobile to)
    {
      if (m_LootType == LootType.Blessed)
        LabelTo(to, 1041362); // (blessed)
      else if (m_LootType == LootType.Cursed)
        LabelTo(to, "(cursed)");
    }

    public bool AtWorldPoint(int x, int y) => m_Parent == null && m_Location.m_X == x && m_Location.m_Y == y;

    public bool AtPoint(int x, int y) => m_Location.m_X == x && m_Location.m_Y == y;

    public virtual bool OnDecay() => Decays && Parent == null && Map != Map.Internal && Region.Find(Location, Map).OnDecay(this);

    public void SetLastMoved()
    {
      LastMoved = DateTime.UtcNow;
    }

    public virtual bool CanStackWith(Item dropped) =>
      dropped.Stackable && Stackable && dropped.GetType() == GetType() && dropped.ItemID == ItemID &&
      dropped.Hue == Hue && dropped.Name == Name && dropped.Amount + Amount <= 60000 && dropped != this;

    public bool StackWith(Mobile from, Item dropped) => StackWith(from, dropped, true);

    public virtual bool StackWith(Mobile from, Item dropped, bool playSound)
    {
      if (CanStackWith(dropped))
      {
        if (m_LootType != dropped.m_LootType)
          m_LootType = LootType.Regular;

        Amount += dropped.Amount;
        dropped.Delete();

        if (playSound && from != null)
        {
          int soundID = GetDropSound();

          if (soundID == -1)
            soundID = 0x42;

          from.SendSound(soundID, GetWorldLocation());
        }

        return true;
      }

      return false;
    }

    public virtual bool OnDragDrop(Mobile from, Item dropped)
    {
      bool success = Parent is Container container && container.OnStackAttempt(from, this, dropped) ||
                     StackWith(from, dropped);

      if (success && Spawner != null)
      {
        Spawner.Remove(this);
        Spawner = null;
      }

      return success;
    }

    public Rectangle2D GetGraphicBounds()
    {
      int itemID = m_ItemID;
      bool doubled = m_Amount > 1;

      if (itemID >= 0xEEA && itemID <= 0xEF2) // Are we coins?
      {
        int coinBase = (itemID - 0xEEA) / 3;
        coinBase *= 3;
        coinBase += 0xEEA;

        doubled = false;

        if (m_Amount <= 1)
          itemID = coinBase;
        else if (m_Amount <= 5)
          itemID = coinBase + 1;
        else // m_Amount > 5
          itemID = coinBase + 2;
      }

      Rectangle2D bounds = ItemBounds.Table[itemID & 0x3FFF];

      if (doubled) bounds.Set(bounds.X, bounds.Y, bounds.Width + 5, bounds.Height + 5);

      return bounds;
    }

    public virtual void AppendChildProperties(ObjectPropertyList list)
    {
      if (m_Parent is Item item)
        item.GetChildProperties(list, this);
      else if (m_Parent is Mobile mobile)
        mobile.GetChildProperties(list, this);
    }

    public virtual void AppendChildNameProperties(ObjectPropertyList list)
    {
      if (m_Parent is Item item)
        item.GetChildNameProperties(list, this);
      else if (m_Parent is Mobile mobile)
        mobile.GetChildNameProperties(list, this);
    }

    public void ClearProperties()
    {
      StaticPacketHandlers.FreeOPLPacket(this);
      StaticPacketHandlers.FreeOPLInfoPacket(this);
    }

    public void InvalidateProperties()
    {
      if (!ObjectPropertyList.Enabled)
        return;

      if (m_Map != null && m_Map != Map.Internal && !World.Loading)
      {
        ObjectPropertyList oldList = StaticPacketHandlers.FreeOPLPacket(this);

        if (oldList?.Hash != PropertyList.Hash)
        {
          StaticPacketHandlers.FreeOPLInfoPacket(this);
          Delta(ItemDelta.Properties);
        }
      }
      else
      {
        ClearProperties();
      }
    }

    public void ReleaseWorldPackets()
    {
      StaticPacketHandlers.FreeWorldItemPackets(this);
    }

    public virtual int GetPacketFlags()
    {
      int flags = 0;

      if (!Visible)
        flags |= 0x80;

      if (Movable || ForceShowProperties)
        flags |= 0x20;

      return flags;
    }

    public virtual bool OnMoveOff(Mobile m) => true;

    public virtual bool OnMoveOver(Mobile m) => true;

    public virtual void OnMovement(Mobile m, Point3D oldLocation)
    {
    }

    public void Internalize()
    {
      MoveToWorld(Point3D.Zero, Map.Internal);
    }

    public virtual void OnMapChange()
    {
    }

    public virtual void OnRemoved(IEntity parent)
    {
    }

    public virtual void OnAdded(IEntity parent)
    {
    }

    private static void SetSaveFlag(ref SaveFlag flags, SaveFlag toSet, bool setIf)
    {
      if (setIf)
        flags |= toSet;
    }

    private static bool GetSaveFlag(SaveFlag flags, SaveFlag toGet) => (flags & toGet) != 0;

    public IPooledEnumerable<IEntity> GetObjectsInRange(int range)
    {
      Map map = m_Map;

      if (map == null)
        return Map.NullEnumerable<IEntity>.Instance;

      if (m_Parent == null)
        return map.GetObjectsInRange(m_Location, range);

      return map.GetObjectsInRange(GetWorldLocation(), range);
    }

    public IPooledEnumerable<Item> GetItemsInRange(int range)
    {
      Map map = m_Map;

      if (map == null)
        return Map.NullEnumerable<Item>.Instance;

      if (m_Parent == null)
        return map.GetItemsInRange(m_Location, range);

      return map.GetItemsInRange(GetWorldLocation(), range);
    }

    public IPooledEnumerable<Mobile> GetMobilesInRange(int range)
    {
      Map map = m_Map;

      if (map == null)
        return Map.NullEnumerable<Mobile>.Instance;

      if (m_Parent == null)
        return map.GetMobilesInRange(m_Location, range);

      return map.GetMobilesInRange(GetWorldLocation(), range);
    }

    public IPooledEnumerable<NetState> GetClientsInRange(int range)
    {
      Map map = m_Map;

      if (map == null)
        return Map.NullEnumerable<NetState>.Instance;

      if (m_Parent == null)
        return map.GetClientsInRange(m_Location, range);

      return map.GetClientsInRange(GetWorldLocation(), range);
    }

    public bool GetTempFlag(int flag) => ((LookupCompactInfo()?.m_TempFlags ?? 0) & flag) != 0;

    public void SetTempFlag(int flag, bool value)
    {
      CompactInfo info = AcquireCompactInfo();

      if (value)
        info.m_TempFlags |= flag;
      else
        info.m_TempFlags &= ~flag;

      if (info.m_TempFlags == 0)
        VerifyCompactInfo();
    }

    public bool GetSavedFlag(int flag) => ((LookupCompactInfo()?.m_SavedFlags ?? 0) & flag) != 0;

    public void SetSavedFlag(int flag, bool value)
    {
      CompactInfo info = AcquireCompactInfo();

      if (value)
        info.m_SavedFlags |= flag;
      else
        info.m_SavedFlags &= ~flag;

      if (info.m_SavedFlags == 0)
        VerifyCompactInfo();
    }

    public virtual void Deserialize(IGenericReader reader)
    {
      int version = reader.ReadInt();

      SetLastMoved();

      switch (version)
      {
        case 9:
        case 8:
        case 7:
        case 6:
        {
          SaveFlag flags = (SaveFlag)reader.ReadInt();

          if (version < 7)
          {
            LastMoved = reader.ReadDeltaTime();
          }
          else
          {
            int minutes = reader.ReadEncodedInt();

            try
            {
              LastMoved = DateTime.UtcNow - TimeSpan.FromMinutes(minutes);
            }
            catch
            {
              LastMoved = DateTime.UtcNow;
            }
          }

          if (GetSaveFlag(flags, SaveFlag.Direction))
            m_Direction = (Direction)reader.ReadByte();

          if (GetSaveFlag(flags, SaveFlag.Bounce))
            AcquireCompactInfo().m_Bounce = BounceInfo.Deserialize(reader);

          if (GetSaveFlag(flags, SaveFlag.LootType))
            m_LootType = (LootType)reader.ReadByte();

          int x = 0, y = 0, z = 0;

          if (GetSaveFlag(flags, SaveFlag.LocationFull))
          {
            x = reader.ReadEncodedInt();
            y = reader.ReadEncodedInt();
            z = reader.ReadEncodedInt();
          }
          else
          {
            if (GetSaveFlag(flags, SaveFlag.LocationByteXY))
            {
              x = reader.ReadByte();
              y = reader.ReadByte();
            }
            else if (GetSaveFlag(flags, SaveFlag.LocationShortXY))
            {
              x = reader.ReadShort();
              y = reader.ReadShort();
            }

            if (GetSaveFlag(flags, SaveFlag.LocationSByteZ))
              z = reader.ReadSByte();
          }

          m_Location = new Point3D(x, y, z);

          if (GetSaveFlag(flags, SaveFlag.ItemID))
            m_ItemID = reader.ReadEncodedInt();

          if (GetSaveFlag(flags, SaveFlag.Hue))
            m_Hue = reader.ReadEncodedInt();

          if (GetSaveFlag(flags, SaveFlag.Amount))
            m_Amount = reader.ReadEncodedInt();
          else
            m_Amount = 1;

          if (GetSaveFlag(flags, SaveFlag.Layer))
            m_Layer = (Layer)reader.ReadByte();

          if (GetSaveFlag(flags, SaveFlag.Name))
          {
            string name = reader.ReadString();

            if (name != DefaultName)
              AcquireCompactInfo().m_Name = name;
          }

          if (GetSaveFlag(flags, SaveFlag.Parent))
          {
            Serial parent = reader.ReadUInt();

            if (parent.IsMobile)
              m_Parent = World.FindMobile(parent);
            else if (parent.IsItem)
              m_Parent = World.FindItem(parent);
            else
              m_Parent = null;

            if (m_Parent == null && (parent.IsMobile || parent.IsItem))
              Delete();
          }

          if (GetSaveFlag(flags, SaveFlag.Items))
          {
            List<Item> items = reader.ReadStrongItemList();

            if (this is Container)
              (this as Container).m_Items = items;
            else
              AcquireCompactInfo().m_Items = items;
          }

          if (version < 8 || !GetSaveFlag(flags, SaveFlag.NullWeight))
          {
            double weight;

            if (GetSaveFlag(flags, SaveFlag.IntWeight))
              weight = reader.ReadEncodedInt();
            else if (GetSaveFlag(flags, SaveFlag.WeightNot1or0))
              weight = reader.ReadDouble();
            else if (GetSaveFlag(flags, SaveFlag.WeightIs0))
              weight = 0.0;
            else
              weight = 1.0;

            if (weight != DefaultWeight)
              AcquireCompactInfo().m_Weight = weight;
          }

          if (GetSaveFlag(flags, SaveFlag.Map))
            m_Map = reader.ReadMap();
          else
            m_Map = Map.Internal;

          if (GetSaveFlag(flags, SaveFlag.Visible))
            SetFlag(ImplFlag.Visible, reader.ReadBool());
          else
            SetFlag(ImplFlag.Visible, true);

          if (GetSaveFlag(flags, SaveFlag.Movable))
            SetFlag(ImplFlag.Movable, reader.ReadBool());
          else
            SetFlag(ImplFlag.Movable, true);

          if (GetSaveFlag(flags, SaveFlag.Stackable))
            SetFlag(ImplFlag.Stackable, reader.ReadBool());

          if (GetSaveFlag(flags, SaveFlag.ImplFlags))
            m_Flags = (ImplFlag)reader.ReadEncodedInt();

          if (GetSaveFlag(flags, SaveFlag.InsuredFor))
            /*m_InsuredFor = */ reader.ReadMobile();

          if (GetSaveFlag(flags, SaveFlag.BlessedFor))
            AcquireCompactInfo().m_BlessedFor = reader.ReadMobile();

          if (GetSaveFlag(flags, SaveFlag.HeldBy))
            AcquireCompactInfo().m_HeldBy = reader.ReadMobile();

          if (GetSaveFlag(flags, SaveFlag.SavedFlags))
            AcquireCompactInfo().m_SavedFlags = reader.ReadEncodedInt();

          if (m_Map != null && m_Parent == null)
            m_Map.OnEnter(this);

          break;
        }
        case 5:
        {
          SaveFlag flags = (SaveFlag)reader.ReadInt();

          LastMoved = reader.ReadDeltaTime();

          if (GetSaveFlag(flags, SaveFlag.Direction))
            m_Direction = (Direction)reader.ReadByte();

          if (GetSaveFlag(flags, SaveFlag.Bounce))
            AcquireCompactInfo().m_Bounce = BounceInfo.Deserialize(reader);

          if (GetSaveFlag(flags, SaveFlag.LootType))
            m_LootType = (LootType)reader.ReadByte();

          if (GetSaveFlag(flags, SaveFlag.LocationFull))
            m_Location = reader.ReadPoint3D();

          if (GetSaveFlag(flags, SaveFlag.ItemID))
            m_ItemID = reader.ReadInt();

          if (GetSaveFlag(flags, SaveFlag.Hue))
            m_Hue = reader.ReadInt();

          if (GetSaveFlag(flags, SaveFlag.Amount))
            m_Amount = reader.ReadInt();
          else
            m_Amount = 1;

          if (GetSaveFlag(flags, SaveFlag.Layer))
            m_Layer = (Layer)reader.ReadByte();

          if (GetSaveFlag(flags, SaveFlag.Name))
          {
            string name = reader.ReadString();

            if (name != DefaultName)
              AcquireCompactInfo().m_Name = name;
          }

          if (GetSaveFlag(flags, SaveFlag.Parent))
          {
            Serial parent = reader.ReadUInt();

            if (parent.IsMobile)
              m_Parent = World.FindMobile(parent);
            else if (parent.IsItem)
              m_Parent = World.FindItem(parent);
            else
              m_Parent = null;

            if (m_Parent == null && (parent.IsMobile || parent.IsItem))
              Delete();
          }

          if (GetSaveFlag(flags, SaveFlag.Items))
          {
            List<Item> items = reader.ReadStrongItemList();

            if (this is Container)
              (this as Container).m_Items = items;
            else
              AcquireCompactInfo().m_Items = items;
          }

          double weight;

          if (GetSaveFlag(flags, SaveFlag.IntWeight))
            weight = reader.ReadEncodedInt();
          else if (GetSaveFlag(flags, SaveFlag.WeightNot1or0))
            weight = reader.ReadDouble();
          else if (GetSaveFlag(flags, SaveFlag.WeightIs0))
            weight = 0.0;
          else
            weight = 1.0;

          if (weight != DefaultWeight)
            AcquireCompactInfo().m_Weight = weight;

          if (GetSaveFlag(flags, SaveFlag.Map))
            m_Map = reader.ReadMap();
          else
            m_Map = Map.Internal;

          if (GetSaveFlag(flags, SaveFlag.Visible))
            SetFlag(ImplFlag.Visible, reader.ReadBool());
          else
            SetFlag(ImplFlag.Visible, true);

          if (GetSaveFlag(flags, SaveFlag.Movable))
            SetFlag(ImplFlag.Movable, reader.ReadBool());
          else
            SetFlag(ImplFlag.Movable, true);

          if (GetSaveFlag(flags, SaveFlag.Stackable))
            SetFlag(ImplFlag.Stackable, reader.ReadBool());

          if (m_Map != null && m_Parent == null)
            m_Map.OnEnter(this);

          break;
        }
        case 4: // Just removed variables
        case 3:
        {
          m_Direction = (Direction)reader.ReadInt();

          goto case 2;
        }
        case 2:
        {
          AcquireCompactInfo().m_Bounce = BounceInfo.Deserialize(reader);
          LastMoved = reader.ReadDeltaTime();

          goto case 1;
        }
        case 1:
        {
          m_LootType = (LootType)reader.ReadByte(); //m_Newbied = reader.ReadBool();

          goto case 0;
        }
        case 0:
        {
          m_Location = reader.ReadPoint3D();
          m_ItemID = reader.ReadInt();
          m_Hue = reader.ReadInt();
          m_Amount = reader.ReadInt();
          m_Layer = (Layer)reader.ReadByte();

          string name = reader.ReadString();

          if (name != DefaultName)
            AcquireCompactInfo().m_Name = name;

          Serial parent = reader.ReadUInt();

          if (parent.IsMobile)
            m_Parent = World.FindMobile(parent);
          else if (parent.IsItem)
            m_Parent = World.FindItem(parent);
          else
            m_Parent = null;

          if (m_Parent == null && (parent.IsMobile || parent.IsItem))
            Delete();

          int count = reader.ReadInt();

          if (count > 0)
          {
            List<Item> items = new List<Item>(count);

            for (int i = 0; i < count; ++i)
            {
              Item item = reader.ReadItem();

              if (item != null)
                items.Add(item);
            }

            if (this is Container)
              (this as Container).m_Items = items;
            else
              AcquireCompactInfo().m_Items = items;
          }

          double weight = reader.ReadDouble();

          if (weight != DefaultWeight)
            AcquireCompactInfo().m_Weight = weight;

          if (version <= 3)
          {
            reader.ReadInt();
            reader.ReadInt();
            reader.ReadInt();
          }

          m_Map = reader.ReadMap();
          SetFlag(ImplFlag.Visible, reader.ReadBool());
          SetFlag(ImplFlag.Movable, reader.ReadBool());

          if (version <= 3)
            /*m_Deleted =*/ reader.ReadBool();

          Stackable = reader.ReadBool();

          if (m_Map != null && m_Parent == null)
            m_Map.OnEnter(this);

          break;
        }
      }

      if (HeldBy != null)
        Timer.DelayCall(TimeSpan.Zero, FixHolding_Sandbox);

      //if ( version < 9 )
      VerifyCompactInfo();
    }

    private void FixHolding_Sandbox()
    {
      Mobile heldBy = HeldBy;

      if (heldBy != null)
      {
        if (GetBounce() != null)
        {
          Bounce(heldBy);
        }
        else
        {
          heldBy.Holding = null;
          heldBy.AddToBackpack(this);
          ClearBounce();
        }
      }
    }

    public virtual int GetMaxUpdateRange() => 18;

    public virtual int GetUpdateRange(Mobile m) => 18;

    public void SendInfoTo(NetState state)
    {
      SendInfoTo(state, ObjectPropertyList.Enabled);
    }

    public virtual void SendInfoTo(NetState state, bool sendOplPacket)
    {
      state.Send(GetWorldPacketFor(state));

      if (sendOplPacket) state.Send(OPLPacket);
    }

    protected virtual Packet GetWorldPacketFor(NetState state)
    {
      if (state.HighSeas)
        return WorldPacketHS;
      if (state.StygianAbyss)
        return WorldPacketSA;
      return WorldPacket;
    }

    public virtual int GetTotal(TotalType type) => 0;

    public virtual void UpdateTotal(Item sender, TotalType type, int delta)
    {
      if (!IsVirtualItem)
      {
        if (m_Parent is Item item)
          item.UpdateTotal(sender, type, delta);
        else if (m_Parent is Mobile mobile)
          mobile.UpdateTotal(sender, type, delta);
        else
          HeldBy?.UpdateTotal(sender, type, delta);
      }
    }

    public virtual void UpdateTotals()
    {
    }

    public virtual void HandleInvalidTransfer(Mobile from)
    {
      // OSI sends 1074769, bug!
      if (QuestItem)
        from.SendLocalizedMessage(
          1049343); // You can only drop quest items into the top-most level of your backpack while you still need them for your quest.
    }

    public bool ParentsContain<T>() where T : Item
    {
      IEntity p = m_Parent;

      while (p is Item item)
      {
        if (item is T)
          return true;

        if (item.m_Parent == null) break;

        p = item.m_Parent;
      }

      return false;
    }

    public virtual void AddItem(Item item)
    {
      if (item?.Deleted != false || item.m_Parent == this) return;

      if (item == this)
      {
        Console.WriteLine("Warning: Adding item to itself: [0x{0:X} {1}].AddItem( [0x{2:X} {3}] )", Serial.Value,
          GetType().Name, item.Serial.Value, item.GetType().Name);
        Console.WriteLine(new StackTrace());
        return;
      }

      if (IsChildOf(item))
      {
        Console.WriteLine("Warning: Adding parent item to child: [0x{0:X} {1}].AddItem( [0x{2:X} {3}] )",
          Serial.Value, GetType().Name, item.Serial.Value, item.GetType().Name);
        Console.WriteLine(new StackTrace());
        return;
      }

      if (item.m_Parent is Mobile parentMobile)
        parentMobile.RemoveItem(item);
      else if (item.m_Parent is Item parentItem)
        parentItem.RemoveItem(item);
      else
        item.SendRemovePacket();

      item.Parent = this;
      item.Map = m_Map;

      List<Item> items = AcquireItems();

      items.Add(item);

      if (!item.IsVirtualItem)
      {
        UpdateTotal(item, TotalType.Gold, item.TotalGold);
        UpdateTotal(item, TotalType.Items, item.TotalItems + 1);
        UpdateTotal(item, TotalType.Weight, item.TotalWeight + item.PileWeight);
      }

      item.Delta(ItemDelta.Update);

      item.OnAdded(this);
      OnItemAdded(item);
    }

    public void Delta(ItemDelta flags)
    {
      if (m_Map == null || m_Map == Map.Internal)
        return;

      m_DeltaFlags |= flags;

      if (!GetFlag(ImplFlag.InQueue))
      {
        SetFlag(ImplFlag.InQueue, true);

        if (_processing)
          try
          {
            using StreamWriter op = new StreamWriter("delta-recursion.log", true);
            op.WriteLine("# {0}", DateTime.UtcNow);
            op.WriteLine(new StackTrace());
            op.WriteLine();
          }
          catch
          {
            // ignored
          }
        else
          m_DeltaQueue.Add(this);
      }

      Core.Set();
    }

    public void RemDelta(ItemDelta flags)
    {
      m_DeltaFlags &= ~flags;

      if (GetFlag(ImplFlag.InQueue) && m_DeltaFlags == ItemDelta.None)
      {
        SetFlag(ImplFlag.InQueue, false);

        if (_processing)
          try
          {
            using StreamWriter op = new StreamWriter("delta-recursion.log", true);
            op.WriteLine("# {0}", DateTime.UtcNow);
            op.WriteLine(new StackTrace());
            op.WriteLine();
          }
          catch
          {
            // ignored
          }
        else
          m_DeltaQueue.Remove(this);
      }
    }

    public static void ProcessDeltaQueue()
    {
      _processing = true;

      if (m_DeltaQueue.Count >= 512)
        Parallel.ForEach(m_DeltaQueue, i => i.ProcessDelta());
      else
        for (int i = 0; i < m_DeltaQueue.Count; i++)
          m_DeltaQueue[i].ProcessDelta();

      m_DeltaQueue.Clear();

      _processing = false;
    }

    public virtual void OnDelete()
    {
      if (Spawner != null)
      {
        Spawner.Remove(this);
        Spawner = null;
      }
    }

    public virtual void OnParentDeleted(IEntity parent)
    {
      Delete();
    }

    public virtual void FreeCache()
    {
      ReleaseWorldPackets();
      StaticPacketHandlers.FreeRemoveItemPacket(this);
      StaticPacketHandlers.FreeOPLInfoPacket(this);
      StaticPacketHandlers.FreeOPLPacket(this);
    }

    public void PublicOverheadMessage(MessageType type, int hue, bool ascii, string text)
    {
      if (m_Map == null)
        return;

      Packet p = null;
      Point3D worldLoc = GetWorldLocation();

      IPooledEnumerable<NetState> eable = m_Map.GetClientsInRange(worldLoc, GetMaxUpdateRange());

      foreach (NetState state in eable)
      {
        Mobile m = state.Mobile;

        if (m.CanSee(this) && m.InRange(worldLoc, GetUpdateRange(m)))
        {
          if (p == null)
          {
            if (ascii)
              p = new AsciiMessage(Serial, m_ItemID, type, hue, 3, Name, text);
            else
              p = new UnicodeMessage(Serial, m_ItemID, type, hue, 3, "ENU", Name, text);

            p.Acquire();
          }

          state.Send(p);
        }
      }

      Packet.Release(p);

      eable.Free();
    }

    public void PublicOverheadMessage(MessageType type, int hue, int number)
    {
      PublicOverheadMessage(type, hue, number, "");
    }

    public void PublicOverheadMessage(MessageType type, int hue, int number, string args)
    {
      if (m_Map == null)
        return;

      Packet p = null;
      Point3D worldLoc = GetWorldLocation();

      IPooledEnumerable<NetState> eable = m_Map.GetClientsInRange(worldLoc, GetMaxUpdateRange());

      foreach (NetState state in eable)
      {
        Mobile m = state.Mobile;

        if (m.CanSee(this) && m.InRange(worldLoc, GetUpdateRange(m)))
        {
          p ??= Packet.Acquire(new MessageLocalized(Serial, m_ItemID, type, hue, 3, number, Name, args));

          state.Send(p);
        }
      }

      Packet.Release(p);

      eable.Free();
    }

    public virtual void OnAfterDelete()
    {
    }

    public virtual void RemoveItem(Item item)
    {
      List<Item> items = LookupItems();

      if (items?.Contains(item) == true)
      {
        item.SendRemovePacket();

        items.Remove(item);

        if (!item.IsVirtualItem)
        {
          UpdateTotal(item, TotalType.Gold, -item.TotalGold);
          UpdateTotal(item, TotalType.Items, -(item.TotalItems + 1));
          UpdateTotal(item, TotalType.Weight, -(item.TotalWeight + item.PileWeight));
        }

        item.Parent = null;

        item.OnRemoved(this);
        OnItemRemoved(item);
      }
    }

    public virtual void OnAfterDuped(Item newItem)
    {
    }

    public virtual bool OnDragLift(Mobile from) => true;

    public virtual bool OnEquip(Mobile from) => true;

    protected virtual void OnAmountChange(int oldValue)
    {
    }

    public virtual void OnSpeech(SpeechEventArgs e)
    {
    }

    public virtual bool OnDroppedToMobile(Mobile from, Mobile target)
    {
      if (Nontransferable && from.Player)
      {
        HandleInvalidTransfer(from);
        return false;
      }

      return true;
    }

    public virtual bool DropToMobile(Mobile from, Mobile target, Point3D p) =>
      !(Deleted || from.Deleted || target.Deleted) && from.Map == target.Map && from.Map != null &&
      target.Map != null && (from.AccessLevel >= AccessLevel.GameMaster || from.InRange(target.Location, 2)) &&
      from.CanSee(target) && from.InLOS(target) && from.OnDroppedItemToMobile(this, target) &&
      OnDroppedToMobile(from, target) && target.OnDragDrop(from, this);

    public virtual bool OnDroppedInto(Mobile from, Container target, Point3D p)
    {
      if (!from.OnDroppedItemInto(this, target, p))
        return false;

      if (Nontransferable && from.Player && target != from.Backpack)
      {
        HandleInvalidTransfer(from);
        return false;
      }

      return target.OnDragDropInto(from, this, p);
    }

    public virtual bool OnDroppedOnto(Mobile from, Item target)
    {
      if (Deleted || from.Deleted || target.Deleted || from.Map != target.Map || from.Map == null ||
          target.Map == null)
        return false;
      if (from.AccessLevel < AccessLevel.GameMaster && !from.InRange(target.GetWorldLocation(), 2))
        return false;
      if (!from.CanSee(target) || !from.InLOS(target))
        return false;
      if (!target.IsAccessibleTo(from))
        return false;
      if (!from.OnDroppedItemOnto(this, target))
        return false;
      if (Nontransferable && from.Player && target != from.Backpack)
      {
        HandleInvalidTransfer(from);
        return false;
      }

      return target.OnDragDrop(from, this);
    }

    public virtual bool DropToItem(Mobile from, Item target, Point3D p)
    {
      if (Deleted || from.Deleted || target.Deleted || from.Map != target.Map || from.Map == null ||
          target.Map == null)
        return false;

      if (from.AccessLevel < AccessLevel.GameMaster && !from.InRange(target.GetWorldLocation(), 2))
        return false;
      if (!from.CanSee(target) || !from.InLOS(target))
        return false;
      if (!target.IsAccessibleTo(from))
        return false;
      if (target.RootParent is Mobile mobile && !mobile.CheckNonlocalDrop(from, this, target))
        return false;
      if (!from.OnDroppedItemToItem(this, target, p))
        return false;
      if (target is Container container && p.m_X != -1 && p.m_Y != -1)
        return OnDroppedInto(from, container, p);

      return OnDroppedOnto(from, target);
    }

    public virtual bool OnDroppedToWorld(Mobile from, Point3D p)
    {
      if (Nontransferable && from.Player)
      {
        HandleInvalidTransfer(from);
        return false;
      }

      return true;
    }

    public virtual int GetLiftSound(Mobile from) => 0x57;

    public virtual bool DropToWorld(Mobile from, Point3D p)
    {
      if (Deleted || from.Deleted || from.Map == null)
        return false;

      if (!from.InRange(p, 2))
        return false;

      Map map = from.Map;

      if (map == null)
        return false;

      int x = p.m_X, y = p.m_Y;
      int z = int.MinValue;

      int maxZ = from.Z + 16;

      LandTile landTile = map.Tiles.GetLandTile(x, y);
      TileFlag landFlags = TileData.LandTable[landTile.ID & TileData.MaxLandValue].Flags;

      int landZ = 0, landAvg = 0, landTop = 0;
      map.GetAverageZ(x, y, ref landZ, ref landAvg, ref landTop);

      if (!landTile.Ignored && (landFlags & TileFlag.Impassable) == 0)
        if (landAvg <= maxZ)
          z = landAvg;

      StaticTile[] tiles = map.Tiles.GetStaticTiles(x, y, true);

      for (int i = 0; i < tiles.Length; ++i)
      {
        StaticTile tile = tiles[i];
        ItemData id = TileData.ItemTable[tile.ID & TileData.MaxItemValue];

        if (!id.Surface)
          continue;

        int top = tile.Z + id.CalcHeight;

        if (top > maxZ || top < z)
          continue;

        z = top;
      }

      IPooledEnumerable<Item> eable = map.GetItemsInRange(p, 0);

      List<Item> items = eable.Where(item =>
      {
        if (item is BaseMulti || item.ItemID > TileData.MaxItemValue)
          return false;

        ItemData id = item.ItemData;

        if (id.Surface)
        {
          int top = item.Z + id.CalcHeight;
          if (top <= maxZ && top >= z)
            z = top;
        }

        return true;
      }).ToList();

      eable.Free();

      if (z == int.MinValue)
        return false;

      if (z > maxZ)
        return false;

      m_OpenSlots = (1 << 20) - 1;

      int surfaceZ = z;

      for (int i = 0; i < tiles.Length; ++i)
      {
        StaticTile tile = tiles[i];
        ItemData id = TileData.ItemTable[tile.ID & TileData.MaxItemValue];

        int checkZ = tile.Z;
        int checkTop = checkZ + id.CalcHeight;

        if (checkTop == checkZ && !id.Surface)
          ++checkTop;

        int zStart = checkZ - z;
        int zEnd = checkTop - z;

        if (zStart >= 20 || zEnd < 0)
          continue;

        if (zStart < 0)
          zStart = 0;

        if (zEnd > 19)
          zEnd = 19;

        int bitCount = zEnd - zStart;

        m_OpenSlots &= ~((1 << bitCount) - 1 << zStart);
      }

      for (int i = 0; i < items.Count; ++i)
      {
        Item item = items[i];
        ItemData id = item.ItemData;

        int checkZ = item.Z;
        int checkTop = checkZ + id.CalcHeight;

        if (checkTop == checkZ && !id.Surface)
          ++checkTop;

        int zStart = checkZ - z;
        int zEnd = checkTop - z;

        if (zStart >= 20 || zEnd < 0)
          continue;

        if (zStart < 0)
          zStart = 0;

        if (zEnd > 19)
          zEnd = 19;

        int bitCount = zEnd - zStart;

        m_OpenSlots &= ~((1 << bitCount) - 1 << zStart);
      }

      int height = ItemData.Height;

      if (height == 0)
        ++height;

      if (height > 30)
        height = 30;

      int match = (1 << height) - 1;
      bool okay = false;

      for (int i = 0; i < 20; ++i)
      {
        if (i + height > 20)
          match >>= 1;

        okay = (m_OpenSlots >> i & match) == match;

        if (okay)
        {
          z += i;
          break;
        }
      }

      if (!okay)
        return false;

      height = ItemData.Height;

      if (height == 0)
        ++height;

      if (landAvg > z && z + height > landZ)
        return false;

      if ((landFlags & TileFlag.Impassable) != 0 && landAvg > surfaceZ && z + height > landZ)
        return false;

      for (int i = 0; i < tiles.Length; ++i)
      {
        StaticTile tile = tiles[i];
        ItemData id = TileData.ItemTable[tile.ID & TileData.MaxItemValue];

        int checkZ = tile.Z;
        int checkTop = checkZ + id.CalcHeight;

        if (checkTop > z && z + height > checkZ)
          return false;

        if ((id.Surface || id.Impassable) && checkTop > surfaceZ && z + height > checkZ)
          return false;
      }

      for (int i = 0; i < items.Count; ++i)
      {
        Item item = items[i];
        ItemData id = item.ItemData;

        //int checkZ = item.Z;
        //int checkTop = checkZ + id.CalcHeight;

        if (item.Z + id.CalcHeight > z && z + height > item.Z)
          return false;
      }

      p = new Point3D(x, y, z);

      if (!from.InLOS(new Point3D(x, y, z + 1)))
        return false;
      if (!from.OnDroppedItemToWorld(this, p))
        return false;
      if (!OnDroppedToWorld(from, p))
        return false;

      int soundID = GetDropSound();

      MoveToWorld(p, from.Map);

      from.SendSound(soundID == -1 ? 0x42 : soundID, GetWorldLocation());

      return true;
    }

    public void SendRemovePacket()
    {
      if (Deleted || m_Map == null)
        return;
      Point3D worldLoc = GetWorldLocation();

      IPooledEnumerable<NetState> eable = m_Map.GetClientsInRange(worldLoc, GetMaxUpdateRange());

      foreach (NetState state in eable)
      {
        Mobile m = state.Mobile;

        if (m.InRange(worldLoc, GetUpdateRange(m))) state.Send(RemovePacket);
      }

      eable.Free();
    }

    public virtual int GetDropSound() => -1;

    public Point3D GetWorldLocation()
    {
      IEntity root = RootParent;

      if (root == null)
        return m_Location;
      return root.Location;

      //return root == null ? m_Location : new Point3D( (IPoint3D) root );
    }

    public Point3D GetSurfaceTop()
    {
      IEntity root = RootParent;

      if (root == null)
        return new Point3D(m_Location.m_X, m_Location.m_Y,
          m_Location.m_Z + (ItemData.Surface ? ItemData.CalcHeight : 0));

      return root.Location;
    }

    public Point3D GetWorldTop() => RootParent?.Location ?? new Point3D(m_Location.m_X, m_Location.m_Y, m_Location.m_Z + ItemData.CalcHeight);

    public void SendLocalizedMessageTo(Mobile to, int number)
    {
      if (Deleted || !to.CanSee(this))
        return;

      to.Send(new MessageLocalized(Serial, ItemID, MessageType.Regular, 0x3B2, 3, number, "", ""));
    }

    public void SendLocalizedMessageTo(Mobile to, int number, string args)
    {
      if (Deleted || !to.CanSee(this))
        return;

      to.Send(new MessageLocalized(Serial, ItemID, MessageType.Regular, 0x3B2, 3, number, "", args));
    }

    public void SendLocalizedMessageTo(Mobile to, int number, AffixType affixType, string affix, string args)
    {
      if (Deleted || !to.CanSee(this))
        return;

      to.Send(new MessageLocalizedAffix(Serial, ItemID, MessageType.Regular, 0x3B2, 3, number, "", affixType, affix,
        args));
    }

    public virtual void OnSnoop(Mobile from)
    {
    }

    public SecureTradeContainer GetSecureTradeCont()
    {
      object p = this;

      while (p is Item item)
      {
        if (item is SecureTradeContainer container)
          return container;

        p = item.m_Parent;
      }

      return null;
    }

    public virtual void OnItemAdded(Item item)
    {
      if (m_Parent is Item parentItem)
        parentItem.OnSubItemAdded(item);
      else if (m_Parent is Mobile parentMobile)
        parentMobile.OnSubItemAdded(item);
    }

    public virtual void OnItemRemoved(Item item)
    {
      if (m_Parent is Item parentItem)
        parentItem.OnSubItemRemoved(item);
      else if (m_Parent is Mobile parentMobile)
        parentMobile.OnSubItemRemoved(item);
    }

    public virtual void OnSubItemAdded(Item item)
    {
      if (m_Parent is Item parentItem)
        parentItem.OnSubItemAdded(item);
      else if (m_Parent is Mobile parentMobile)
        parentMobile.OnSubItemAdded(item);
    }

    public virtual void OnSubItemRemoved(Item item)
    {
      if (m_Parent is Item parentItem)
        parentItem.OnSubItemRemoved(item);
      else if (m_Parent is Mobile parentMobile)
        parentMobile.OnSubItemRemoved(item);
    }

    public virtual void OnItemBounceCleared(Item item)
    {
      if (m_Parent is Item parentItem)
        parentItem.OnSubItemBounceCleared(item);
      else if (m_Parent is Mobile parentMobile)
        parentMobile.OnSubItemBounceCleared(item);
    }

    public virtual void OnSubItemBounceCleared(Item item)
    {
      if (m_Parent is Item parentItem)
        parentItem.OnSubItemBounceCleared(item);
      else if (m_Parent is Mobile parentMobile)
        parentMobile.OnSubItemBounceCleared(item);
    }

    public virtual bool CheckTarget(Mobile from, Target targ, object targeted)
    {
      if (m_Parent is Item item)
        return item.CheckTarget(from, targ, targeted);
      if (m_Parent is Mobile mobile)
        return mobile.CheckTarget(from, targ, targeted);

      return true;
    }

    public virtual bool IsAccessibleTo(Mobile check)
    {
      if (m_Parent is Item item)
        return item.IsAccessibleTo(check);

      Region reg = Region.Find(GetWorldLocation(), m_Map);

      return reg.CheckAccessibility(this, check);

      /*SecureTradeContainer cont = GetSecureTradeCont();

      if ( cont != null && !cont.IsChildOf( check ) )
        return false;

      return true;*/
    }

    public bool IsChildOf(IEntity o) => IsChildOf(o, false);

    public bool IsChildOf(IEntity o, bool allowNull)
    {
      IEntity p = m_Parent;

      if ((p == null || o == null) && !allowNull)
        return false;

      if (p == o)
        return true;

      while (p is Item item)
      {
        if (item.m_Parent == null)
          break;

        p = item.m_Parent;

        if (p == o)
          return true;
      }

      return false;
    }

    public virtual void OnItemUsed(Mobile from, Item item)
    {
      if (m_Parent is Item parentItem)
        parentItem.OnItemUsed(from, item);
      else if (m_Parent is Mobile parentMobile)
        parentMobile.OnItemUsed(from, item);
    }

    public bool CheckItemUse(Mobile from) => CheckItemUse(from, this);

    public virtual bool CheckItemUse(Mobile from, Item item)
    {
      if (m_Parent is Item parentItem)
        return parentItem.CheckItemUse(from, item);
      if (m_Parent is Mobile parentMobile)
        return parentMobile.CheckItemUse(from, item);
      return true;
    }

    public virtual void OnItemLifted(Mobile from, Item item)
    {
      if (m_Parent is Item parentItem)
        parentItem.OnItemLifted(from, item);
      else if (m_Parent is Mobile parentMobile)
        parentMobile.OnItemLifted(from, item);
    }

    public bool CheckLift(Mobile from)
    {
      LRReason reject = LRReason.Inspecific;

      return CheckLift(from, this, ref reject);
    }

    public virtual bool CheckLift(Mobile from, Item item, ref LRReason reject)
    {
      if (m_Parent is Item parentItem)
        return parentItem.CheckLift(from, item, ref reject);

      if (m_Parent is Mobile parentMobile)
        return parentMobile.CheckLift(from, item, ref reject);

      return true;
    }

    public virtual void OnSingleClickContained(Mobile from, Item item)
    {
      if (m_Parent is Item item1)
        item1.OnSingleClickContained(from, item);
    }

    public virtual void OnAosSingleClick(Mobile from)
    {
      ObjectPropertyList opl = PropertyList;

      if (opl.Header > 0)
        from.Send(new MessageLocalized(Serial, m_ItemID, MessageType.Label, 0x3B2, 3, opl.Header, Name,
          opl.HeaderArgs));
    }

    public virtual void OnSingleClick(Mobile from)
    {
      if (Deleted || !from.CanSee(this))
        return;

      if (DisplayLootType)
        LabelLootTypeTo(from);

      NetState ns = from.NetState;

      if (ns == null)
        return;

      if (Name == null)
      {
        if (m_Amount <= 1)
          ns.Send(new MessageLocalized(Serial, m_ItemID, MessageType.Label, 0x3B2, 3, LabelNumber, "", ""));
        else
          ns.Send(new MessageLocalizedAffix(Serial, m_ItemID, MessageType.Label, 0x3B2, 3, LabelNumber, "",
            AffixType.Append,
            $" : {m_Amount}", ""));
      }
      else
      {
        ns.Send(new UnicodeMessage(Serial, m_ItemID, MessageType.Label, 0x3B2, 3, "ENU", "",
          Name + (m_Amount > 1 ? $" : {m_Amount}" : "")));
      }
    }

    public virtual void ScissorHelper(Mobile from, Item newItem, int amountPerOldItem)
    {
      ScissorHelper(from, newItem, amountPerOldItem, true);
    }

    public virtual void ScissorHelper(Mobile from, Item newItem, int amountPerOldItem, bool carryHue)
    {
      int amount = Amount;

      if (amount > 60000 / amountPerOldItem) // let's not go over 60000
        amount = 60000 / amountPerOldItem;

      Amount -= amount;

      int ourHue = Hue;
      Map thisMap = Map;
      IEntity thisParent = m_Parent;
      Point3D worldLoc = GetWorldLocation();
      LootType type = LootType;

      if (Amount == 0)
        Delete();

      newItem.Amount = amount * amountPerOldItem;

      if (carryHue)
        newItem.Hue = ourHue;

      if (ScissorCopyLootType)
        newItem.LootType = type;

      if ((thisParent as Container)?.TryDropItem(from, newItem, false) != true)
        newItem.MoveToWorld(worldLoc, thisMap);
    }

    public virtual void Consume()
    {
      Consume(1);
    }

    public virtual void Consume(int amount)
    {
      Amount -= amount;

      if (Amount <= 0)
        Delete();
    }

    public virtual void ReplaceWith(Item newItem)
    {
      if (m_Parent is Container container)
      {
        container.AddItem(newItem);
        newItem.Location = m_Location;
      }
      else
      {
        newItem.MoveToWorld(GetWorldLocation(), m_Map);
      }

      Delete();
    }

    public virtual bool CheckBlessed(Mobile m)
    {
      if (m_LootType == LootType.Blessed || Mobile.InsuranceEnabled && Insured)
        return true;

      return m != null && m == BlessedFor;
    }

    public virtual bool CheckNewbied() => m_LootType == LootType.Newbied;

    public virtual bool IsStandardLoot()
    {
      if (Mobile.InsuranceEnabled && Insured)
        return false;

      if (BlessedFor != null)
        return false;

      return m_LootType == LootType.Regular;
    }

    public override string ToString() => $"0x{Serial.Value:X} \"{GetType().Name}\"";

    public virtual void OnSectorActivate()
    {
    }

    public virtual void OnSectorDeactivate()
    {
    }

    [Flags]
    private enum ImplFlag : byte
    {
      None = 0x00,
      Visible = 0x01,
      Movable = 0x02,
      Deleted = 0x04,
      Stackable = 0x08,
      InQueue = 0x10,
      Insured = 0x20,
      PaidInsurance = 0x40,
      QuestItem = 0x80
    }

    private class CompactInfo
    {
      public Mobile m_BlessedFor;
      public BounceInfo m_Bounce;

      public Mobile m_HeldBy;

      public List<Item> m_Items;
      public string m_Name;
      public int m_SavedFlags;

      public ISpawner m_Spawner;

      public int m_TempFlags;

      public double m_Weight = -1;
    }

    [Flags]
    private enum SaveFlag
    {
      None = 0x00000000,
      Direction = 0x00000001,
      Bounce = 0x00000002,
      LootType = 0x00000004,
      LocationFull = 0x00000008,
      ItemID = 0x00000010,
      Hue = 0x00000020,
      Amount = 0x00000040,
      Layer = 0x00000080,
      Name = 0x00000100,
      Parent = 0x00000200,
      Items = 0x00000400,
      WeightNot1or0 = 0x00000800,
      Map = 0x00001000,
      Visible = 0x00002000,
      Movable = 0x00004000,
      Stackable = 0x00008000,
      WeightIs0 = 0x00010000,
      LocationSByteZ = 0x00020000,
      LocationShortXY = 0x00040000,
      LocationByteXY = 0x00080000,
      ImplFlags = 0x00100000,
      InsuredFor = 0x00200000,
      BlessedFor = 0x00400000,
      HeldBy = 0x00800000,
      IntWeight = 0x01000000,
      SavedFlags = 0x02000000,
      NullWeight = 0x04000000
    }

    #region Standard fields

    private Point3D m_Location;
    private int m_ItemID;
    private int m_Hue;
    private int m_Amount;
    private Layer m_Layer;
    private IEntity m_Parent; // Mobile, Item, or null=World
    private Map m_Map;
    private LootType m_LootType;
    private Direction m_Direction;

    #endregion

    #region Location Location Location!

    public virtual void OnLocationChange(Point3D oldLocation)
    {
    }

    [CommandProperty(AccessLevel.Counselor, AccessLevel.GameMaster)]
    public virtual Point3D Location
    {
      get => m_Location;
      set
      {
        Point3D oldLocation = m_Location;

        if (oldLocation == value)
          return;
        if (m_Map != null)
        {
          if (m_Parent == null)
          {
            IPooledEnumerable<NetState> eable;

            if (m_Location.m_X != 0)
            {
              eable = m_Map.GetClientsInRange(oldLocation, GetMaxUpdateRange());

              foreach (NetState state in eable)
              {
                Mobile m = state.Mobile;

                if (!m.InRange(value, GetUpdateRange(m))) state.Send(RemovePacket);
              }

              eable.Free();
            }

            Point3D oldLoc = m_Location;
            m_Location = value;
            ReleaseWorldPackets();

            SetLastMoved();

            eable = m_Map.GetClientsInRange(m_Location, GetMaxUpdateRange());

            foreach (NetState state in eable)
            {
              Mobile m = state.Mobile;

              if (m.CanSee(this) && m.InRange(m_Location, GetUpdateRange(m)) &&
                  (!state.HighSeas || !NoMoveHS || (m_DeltaFlags & ItemDelta.Update) != 0 ||
                   !m.InRange(oldLoc, GetUpdateRange(m))))
                SendInfoTo(state);
            }

            eable.Free();

            RemDelta(ItemDelta.Update);
          }
          else if (m_Parent is Item)
          {
            m_Location = value;
            ReleaseWorldPackets();

            Delta(ItemDelta.Update);
          }
          else
          {
            m_Location = value;
            ReleaseWorldPackets();
          }

          if (m_Parent == null)
            m_Map.OnMove(oldLocation, this);
        }
        else
        {
          m_Location = value;
          ReleaseWorldPackets();
        }

        OnLocationChange(oldLocation);
      }
    }

    [CommandProperty(AccessLevel.Counselor, AccessLevel.GameMaster)]
    public int X
    {
      get => m_Location.m_X;
      set => Location = new Point3D(value, m_Location.m_Y, m_Location.m_Z);
    }

    [CommandProperty(AccessLevel.Counselor, AccessLevel.GameMaster)]
    public int Y
    {
      get => m_Location.m_Y;
      set => Location = new Point3D(m_Location.m_X, value, m_Location.m_Z);
    }

    [CommandProperty(AccessLevel.Counselor, AccessLevel.GameMaster)]
    public int Z
    {
      get => m_Location.m_Z;
      set => Location = new Point3D(m_Location.m_X, m_Location.m_Y, value);
    }

    #endregion

    #region OnDoubleClick[...]

    public virtual void OnDoubleClick(Mobile from)
    {
    }

    public virtual void OnDoubleClickOutOfRange(Mobile from)
    {
    }

    public virtual void OnDoubleClickCantSee(Mobile from)
    {
    }

    public virtual void OnDoubleClickDead(Mobile from)
    {
      from.LocalOverheadMessage(MessageType.Regular, 0x3B2, 1019048); // I am dead and cannot do that.
    }

    public virtual void OnDoubleClickNotAccessible(Mobile from)
    {
      from.SendLocalizedMessage(500447); // That is not accessible.
    }

    public virtual void OnDoubleClickSecureTrade(Mobile from)
    {
      from.SendLocalizedMessage(500447); // That is not accessible.
    }

    #endregion
  }
}
