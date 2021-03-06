namespace Server.PathAlgorithms
{
  public abstract class PathAlgorithm
  {
    private static Direction[] m_CalcDirections = {
      Direction.Up,
      Direction.North,
      Direction.Right,
      Direction.West,
      Direction.North,
      Direction.East,
      Direction.Left,
      Direction.South,
      Direction.Down
    };

    public abstract bool CheckCondition(Mobile m, Map map, Point3D start, Point3D goal);
    public abstract Direction[] Find(Mobile m, Map map, Point3D start, Point3D goal);

    public Direction GetDirection(int xSource, int ySource, int xDest, int yDest)
    {
      int x = xDest + 1 - xSource;
      int y = yDest + 1 - ySource;
      int v = y * 3 + x;

      if (v < 0 || v >= 9)
        return Direction.North;

      return m_CalcDirections[v];
    }
  }
}