/***************************************************************************
 *                               Point3DList.cs
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

namespace Server
{
  public class Point3DList
  {
    private static Point3D[] m_EmptyList = new Point3D[0];
    private Point3D[] m_List;

    public Point3DList()
    {
      m_List = new Point3D[8];
      Count = 0;
    }

    public int Count{ get; private set; }

    public Point3D Last => m_List[Count - 1];

    public Point3D this[int index] => m_List[index];

    public void Clear()
    {
      Count = 0;
    }

    public void Add(int x, int y, int z)
    {
      if (Count + 1 > m_List.Length)
      {
        Point3D[] old = m_List;
        m_List = new Point3D[old.Length * 2];

        for (int i = 0; i < old.Length; ++i)
          m_List[i] = old[i];
      }

      m_List[Count].m_X = x;
      m_List[Count].m_Y = y;
      m_List[Count].m_Z = z;
      ++Count;
    }

    public void Add(Point3D p)
    {
      if (Count + 1 > m_List.Length)
      {
        Point3D[] old = m_List;
        m_List = new Point3D[old.Length * 2];

        for (int i = 0; i < old.Length; ++i)
          m_List[i] = old[i];
      }

      m_List[Count].m_X = p.m_X;
      m_List[Count].m_Y = p.m_Y;
      m_List[Count].m_Z = p.m_Z;
      ++Count;
    }

    public Point3D[] ToArray()
    {
      if (Count == 0)
        return m_EmptyList;

      Point3D[] list = new Point3D[Count];

      for (int i = 0; i < Count; ++i)
        list[i] = m_List[i];

      Count = 0;

      return list;
    }
  }
}