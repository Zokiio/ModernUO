/*************************************************************************
 * ModernUO                                                              *
 * Copyright (C) 2019 - ModernUO Development Team                        *
 * Email: hi@modernuo.com                                                *
 * File: BinaryFileWriter.cs                                             *
 * Created: 2019/12/30 - Updated: 2020/01/18                             *
 *                                                                       *
 * This program is free software: you can redistribute it and/or modify  *
 * it under the terms of the GNU General Public License as published by  *
 * the Free Software Foundation, either version 3 of the License, or     *
 * (at your option) any later version.                                   *
 *                                                                       *
 * This program is distributed in the hope that it will be useful,       *
 * but WITHOUT ANY WARRANTY; without even the implied warranty of        *
 * MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.  See the         *
 * GNU General Public License for more details.                          *
 *                                                                       *
 * You should have received a copy of the GNU General Public License     *
 * along with this program.  If not, see <http://www.gnu.org/licenses/>. *
 *************************************************************************/

using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Net;
using Server.Guilds;

namespace Server
{
  public class BinaryFileWriter : IGenericWriter
  {
    private const int LargeByteBufferSize = 256;

    private byte[] m_Buffer;

    private byte[] m_CharacterBuffer;

    private Encoding m_Encoding;
    private Stream m_File;

    private int m_Index;
    private int m_MaxBufferChars;

    private long m_Position;

    private char[] m_SingleCharBuffer = new char[1];
    private bool PrefixStrings;

    public BinaryFileWriter(Stream strm, bool prefixStr)
    {
      PrefixStrings = prefixStr;
      m_Encoding = Utility.UTF8;
      m_Buffer = new byte[BufferSize];
      m_File = strm;
    }

    public BinaryFileWriter(string filename, bool prefixStr)
    {
      PrefixStrings = prefixStr;
      m_Buffer = new byte[BufferSize];
      m_File = new FileStream(filename, FileMode.Create, FileAccess.Write, FileShare.None);
      m_Encoding = Utility.UTF8WithEncoding;
    }

    protected virtual int BufferSize => 64 * 1024;

    public long Position => m_Position + m_Index;

    public Stream UnderlyingStream
    {
      get
      {
        if (m_Index > 0)
          Flush();

        return m_File;
      }
    }

    public void Flush()
    {
      if (m_Index > 0)
      {
        m_Position += m_Index;

        m_File.Write(m_Buffer, 0, m_Index);
        m_Index = 0;
      }
    }

    public void Close()
    {
      if (m_Index > 0)
        Flush();

      m_File.Close();
    }

    public void WriteEncodedInt(int value)
    {
      uint v = (uint)value;

      while (v >= 0x80)
      {
        if (m_Index + 1 > m_Buffer.Length)
          Flush();

        m_Buffer[m_Index++] = (byte)(v | 0x80);
        v >>= 7;
      }

      if (m_Index + 1 > m_Buffer.Length)
        Flush();

      m_Buffer[m_Index++] = (byte)v;
    }

    internal void InternalWriteString(string value)
    {
      int length = m_Encoding.GetByteCount(value);

      WriteEncodedInt(length);

      if (m_CharacterBuffer == null)
      {
        m_CharacterBuffer = new byte[LargeByteBufferSize];
        m_MaxBufferChars = LargeByteBufferSize / m_Encoding.GetMaxByteCount(1);
      }

      if (length > LargeByteBufferSize)
      {
        int current = 0;
        int charsLeft = value.Length;

        while (charsLeft > 0)
        {
          int charCount = charsLeft > m_MaxBufferChars ? m_MaxBufferChars : charsLeft;
          int byteLength = m_Encoding.GetBytes(value, current, charCount, m_CharacterBuffer, 0);

          if (m_Index + byteLength > m_Buffer.Length)
            Flush();

          Buffer.BlockCopy(m_CharacterBuffer, 0, m_Buffer, m_Index, byteLength);
          m_Index += byteLength;

          current += charCount;
          charsLeft -= charCount;
        }
      }
      else
      {
        int byteLength = m_Encoding.GetBytes(value, 0, value.Length, m_CharacterBuffer, 0);

        if (m_Index + byteLength > m_Buffer.Length)
          Flush();

        Buffer.BlockCopy(m_CharacterBuffer, 0, m_Buffer, m_Index, byteLength);
        m_Index += byteLength;
      }
    }

    public void Write(string value)
    {
      if (PrefixStrings)
      {
        if (value == null)
        {
          if (m_Index + 1 > m_Buffer.Length)
            Flush();

          m_Buffer[m_Index++] = 0;
        }
        else
        {
          if (m_Index + 1 > m_Buffer.Length)
            Flush();

          m_Buffer[m_Index++] = 1;

          InternalWriteString(value);
        }
      }
      else
      {
        InternalWriteString(value);
      }
    }

    public void Write(DateTime value)
    {
      Write(value.Ticks);
    }

    public void Write(DateTimeOffset value)
    {
      Write(value.Ticks);
      Write(value.Offset.Ticks);
    }

    public void WriteDeltaTime(DateTime value)
    {
      long ticks = value.Ticks;
      long now = DateTime.UtcNow.Ticks;

      TimeSpan d;

      try
      {
        d = new TimeSpan(ticks - now);
      }
      catch
      {
        d = TimeSpan.MaxValue;
      }

      Write(d);
    }

    public void Write(IPAddress value)
    {
      Write(Utility.GetLongAddressValue(value));
    }

    public void Write(TimeSpan value)
    {
      Write(value.Ticks);
    }

    public void Write(decimal value)
    {
      int[] bits = decimal.GetBits(value);

      for (int i = 0; i < bits.Length; ++i)
        Write(bits[i]);
    }

    public void Write(long value)
    {
      if (m_Index + 8 > m_Buffer.Length)
        Flush();

      m_Buffer[m_Index] = (byte)value;
      m_Buffer[m_Index + 1] = (byte)(value >> 8);
      m_Buffer[m_Index + 2] = (byte)(value >> 16);
      m_Buffer[m_Index + 3] = (byte)(value >> 24);
      m_Buffer[m_Index + 4] = (byte)(value >> 32);
      m_Buffer[m_Index + 5] = (byte)(value >> 40);
      m_Buffer[m_Index + 6] = (byte)(value >> 48);
      m_Buffer[m_Index + 7] = (byte)(value >> 56);
      m_Index += 8;
    }

    public void Write(ulong value)
    {
      if (m_Index + 8 > m_Buffer.Length)
        Flush();

      m_Buffer[m_Index] = (byte)value;
      m_Buffer[m_Index + 1] = (byte)(value >> 8);
      m_Buffer[m_Index + 2] = (byte)(value >> 16);
      m_Buffer[m_Index + 3] = (byte)(value >> 24);
      m_Buffer[m_Index + 4] = (byte)(value >> 32);
      m_Buffer[m_Index + 5] = (byte)(value >> 40);
      m_Buffer[m_Index + 6] = (byte)(value >> 48);
      m_Buffer[m_Index + 7] = (byte)(value >> 56);
      m_Index += 8;
    }

    public void Write(int value)
    {
      if (m_Index + 4 > m_Buffer.Length)
        Flush();

      m_Buffer[m_Index] = (byte)value;
      m_Buffer[m_Index + 1] = (byte)(value >> 8);
      m_Buffer[m_Index + 2] = (byte)(value >> 16);
      m_Buffer[m_Index + 3] = (byte)(value >> 24);
      m_Index += 4;
    }

    public void Write(uint value)
    {
      if (m_Index + 4 > m_Buffer.Length)
        Flush();

      m_Buffer[m_Index] = (byte)value;
      m_Buffer[m_Index + 1] = (byte)(value >> 8);
      m_Buffer[m_Index + 2] = (byte)(value >> 16);
      m_Buffer[m_Index + 3] = (byte)(value >> 24);
      m_Index += 4;
    }

    public void Write(short value)
    {
      if (m_Index + 2 > m_Buffer.Length)
        Flush();

      m_Buffer[m_Index] = (byte)value;
      m_Buffer[m_Index + 1] = (byte)(value >> 8);
      m_Index += 2;
    }

    public void Write(ushort value)
    {
      if (m_Index + 2 > m_Buffer.Length)
        Flush();

      m_Buffer[m_Index] = (byte)value;
      m_Buffer[m_Index + 1] = (byte)(value >> 8);
      m_Index += 2;
    }

    public unsafe void Write(double value)
    {
      if (m_Index + 8 > m_Buffer.Length)
        Flush();

      fixed (byte* pBuffer = m_Buffer)
      {
        *(double*)(pBuffer + m_Index) = value;
      }

      m_Index += 8;
    }

    public unsafe void Write(float value)
    {
      if (m_Index + 4 > m_Buffer.Length)
        Flush();

      fixed (byte* pBuffer = m_Buffer)
      {
        *(float*)(pBuffer + m_Index) = value;
      }

      m_Index += 4;
    }

    public void Write(char value)
    {
      if (m_Index + 8 > m_Buffer.Length)
        Flush();

      m_SingleCharBuffer[0] = value;

      int byteCount = m_Encoding.GetBytes(m_SingleCharBuffer, 0, 1, m_Buffer, m_Index);
      m_Index += byteCount;
    }

    public void Write(byte value)
    {
      if (m_Index + 1 > m_Buffer.Length)
        Flush();

      m_Buffer[m_Index++] = value;
    }

    public void Write(byte[] value)
    {
      Write(value, value.Length);
    }

    public void Write(byte[] value, int length)
    {
      if (m_Index + length > m_Buffer.Length)
        Flush();

      Buffer.BlockCopy(value, 0, m_Buffer, m_Index, length);
      m_Index += length;
    }
    public void Write(sbyte value)
    {
      if (m_Index + 1 > m_Buffer.Length)
        Flush();

      m_Buffer[m_Index++] = (byte)value;
    }

    public void Write(bool value)
    {
      if (m_Index + 1 > m_Buffer.Length)
        Flush();

      m_Buffer[m_Index++] = (byte)(value ? 1 : 0);
    }

    public void Write(Point3D value)
    {
      Write(value.m_X);
      Write(value.m_Y);
      Write(value.m_Z);
    }

    public void Write(Point2D value)
    {
      Write(value.m_X);
      Write(value.m_Y);
    }

    public void Write(Rectangle2D value)
    {
      Write(value.Start);
      Write(value.End);
    }

    public void Write(Rectangle3D value)
    {
      Write(value.Start);
      Write(value.End);
    }

    public void Write(Map value)
    {
      if (value != null)
        Write((byte)value.MapIndex);
      else
        Write((byte)0xFF);
    }

    public void Write(Race value)
    {
      if (value != null)
        Write((byte)value.RaceIndex);
      else
        Write((byte)0xFF);
    }

    public void WriteEntity(IEntity value)
    {
      if (value?.Deleted != false)
        Write(Serial.MinusOne);
      else
        Write(value.Serial);
    }

    public void Write(Item value)
    {
      if (value?.Deleted != false)
        Write(Serial.MinusOne);
      else
        Write(value.Serial);
    }

    public void Write(Mobile value)
    {
      if (value?.Deleted != false)
        Write(Serial.MinusOne);
      else
        Write(value.Serial);
    }

    public void Write(BaseGuild value)
    {
      if (value == null)
        Write(0);
      else
        Write(value.Id);
    }

    public void WriteItem<T>(T value) where T : Item
    {
      Write(value);
    }

    public void WriteMobile<T>(T value) where T : Mobile
    {
      Write(value);
    }

    public void WriteGuild<T>(T value) where T : BaseGuild
    {
      Write(value);
    }

    public void Write(List<Item> list)
    {
      Write(list, false);
    }

    public void Write(List<Item> list, bool tidy)
    {
      if (tidy)
        for (int i = 0; i < list.Count;)
          if (list[i].Deleted)
            list.RemoveAt(i);
          else
            ++i;

      Write(list.Count);

      for (int i = 0; i < list.Count; ++i)
        Write(list[i]);
    }

    public void WriteItemList<T>(List<T> list) where T : Item
    {
      WriteItemList(list, false);
    }

    public void WriteItemList<T>(List<T> list, bool tidy) where T : Item
    {
      if (tidy)
        for (int i = 0; i < list.Count;)
          if (list[i].Deleted)
            list.RemoveAt(i);
          else
            ++i;

      Write(list.Count);

      for (int i = 0; i < list.Count; ++i)
        Write(list[i]);
    }

    public void Write(HashSet<Item> set)
    {
      Write(set, false);
    }

    public void Write(HashSet<Item> set, bool tidy)
    {
      if (tidy) set.RemoveWhere(item => item.Deleted);

      Write(set.Count);

      foreach (Item item in set) Write(item);
    }

    public void WriteItemSet<T>(HashSet<T> set) where T : Item
    {
      WriteItemSet(set, false);
    }

    public void WriteItemSet<T>(HashSet<T> set, bool tidy) where T : Item
    {
      if (tidy) set.RemoveWhere(item => item.Deleted);

      Write(set.Count);

      foreach (T item in set) Write(item);
    }

    public void Write(List<Mobile> list)
    {
      Write(list, false);
    }

    public void Write(List<Mobile> list, bool tidy)
    {
      if (tidy)
        for (int i = 0; i < list.Count;)
          if (list[i].Deleted)
            list.RemoveAt(i);
          else
            ++i;

      Write(list.Count);

      for (int i = 0; i < list.Count; ++i)
        Write(list[i]);
    }

    public void WriteMobileList<T>(List<T> list) where T : Mobile
    {
      WriteMobileList(list, false);
    }

    public void WriteMobileList<T>(List<T> list, bool tidy) where T : Mobile
    {
      if (tidy)
        for (int i = 0; i < list.Count;)
          if (list[i].Deleted)
            list.RemoveAt(i);
          else
            ++i;

      Write(list.Count);

      for (int i = 0; i < list.Count; ++i)
        Write(list[i]);
    }

    public void Write(HashSet<Mobile> set)
    {
      Write(set, false);
    }

    public void Write(HashSet<Mobile> set, bool tidy)
    {
      if (tidy) set.RemoveWhere(mobile => mobile.Deleted);

      Write(set.Count);

      foreach (Mobile mob in set) Write(mob);
    }

    public void WriteMobileSet<T>(HashSet<T> set) where T : Mobile
    {
      WriteMobileSet(set, false);
    }

    public void WriteMobileSet<T>(HashSet<T> set, bool tidy) where T : Mobile
    {
      if (tidy) set.RemoveWhere(mob => mob.Deleted);

      Write(set.Count);

      foreach (T mob in set) Write(mob);
    }

    public void Write(List<BaseGuild> list)
    {
      Write(list, false);
    }

    public void Write(List<BaseGuild> list, bool tidy)
    {
      if (tidy)
        for (int i = 0; i < list.Count;)
          if (list[i].Disbanded)
            list.RemoveAt(i);
          else
            ++i;

      Write(list.Count);

      for (int i = 0; i < list.Count; ++i)
        Write(list[i]);
    }

    public void WriteGuildList<T>(List<T> list) where T : BaseGuild
    {
      WriteGuildList(list, false);
    }

    public void WriteGuildList<T>(List<T> list, bool tidy) where T : BaseGuild
    {
      if (tidy)
        for (int i = 0; i < list.Count;)
          if (list[i].Disbanded)
            list.RemoveAt(i);
          else
            ++i;

      Write(list.Count);

      for (int i = 0; i < list.Count; ++i)
        Write(list[i]);
    }

    public void Write(HashSet<BaseGuild> set)
    {
      Write(set, false);
    }

    public void Write(HashSet<BaseGuild> set, bool tidy)
    {
      if (tidy) set.RemoveWhere(guild => guild.Disbanded);

      Write(set.Count);

      foreach (BaseGuild guild in set) Write(guild);
    }

    public void WriteGuildSet<T>(HashSet<T> set) where T : BaseGuild
    {
      WriteGuildSet(set, false);
    }

    public void WriteGuildSet<T>(HashSet<T> set, bool tidy) where T : BaseGuild
    {
      if (tidy) set.RemoveWhere(guild => guild.Disbanded);

      Write(set.Count);

      foreach (T guild in set) Write(guild);
    }
  }

}
