import { MACHINE_STATUSES, STATUS_COLORS, STATUS_LABELS } from '@monitoreoti/shared';
import { StatusBar } from 'expo-status-bar';
import { StyleSheet, Text, View, useColorScheme } from 'react-native';

export default function App() {
  const dark = useColorScheme() === 'dark';
  const textColor = dark ? '#e2e8f0' : '#0f172a';

  return (
    <View style={[styles.container, { backgroundColor: dark ? '#0f172a' : '#f8fafc' }]}>
      <Text style={styles.brand}>MonitoreoTI</Text>
      <Text style={[styles.subtitle, { color: textColor }]}>Fase 0: estructura base de la app.</Text>
      <View style={styles.legend}>
        {MACHINE_STATUSES.map((status) => (
          <View key={status} style={styles.item}>
            <View style={[styles.dot, { backgroundColor: STATUS_COLORS[status] }]} />
            <Text style={{ color: textColor }}>{STATUS_LABELS[status]}</Text>
          </View>
        ))}
      </View>
      <StatusBar style="auto" />
    </View>
  );
}

const styles = StyleSheet.create({
  container: {
    flex: 1,
    alignItems: 'center',
    justifyContent: 'center',
    padding: 24,
  },
  brand: {
    fontSize: 24,
    fontWeight: '600',
    color: '#2563eb',
  },
  subtitle: {
    marginTop: 8,
    marginBottom: 24,
  },
  legend: {
    gap: 12,
  },
  item: {
    flexDirection: 'row',
    alignItems: 'center',
    gap: 8,
  },
  dot: {
    width: 12,
    height: 12,
    borderRadius: 6,
  },
});
